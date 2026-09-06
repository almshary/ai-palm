from __future__ import annotations

import asyncio
import hashlib
import json
import re
import secrets
import uuid
from contextlib import asynccontextmanager, suppress
from datetime import datetime, timedelta, timezone
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles
from sqlalchemy import func, or_, select
from sqlalchemy.ext.asyncio import AsyncSession
from starlette.middleware.trustedhost import TrustedHostMiddleware

from .config import get_settings
from .database import SessionLocal, create_schema, get_session
from .models import Device, Job, utcnow
from .realtime import RealtimeStore
from .schemas import Heartbeat, HostRegistration, JobComplete, JobCreate, JobFail, JobStream


settings = get_settings()
realtime = RealtimeStore(settings.redis_url)
HOST_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{8,64}$")
FINAL_JOB_STATES = {"completed", "failed"}


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def timestamp(value: datetime | None) -> float | None:
    return value.timestamp() if value else None


def public_device(device: Device, online: bool = True) -> dict[str, Any]:
    return {
        "id": device.id,
        "name": device.name,
        "gpu": device.gpu,
        "gpu_vendor": device.gpu_vendor,
        "vram_gb": device.vram_gb,
        "mode": device.mode,
        "model": device.model,
        "capabilities": device.capabilities,
        "enabled": device.enabled,
        "busy": device.busy,
        "online": online,
        "last_seen": timestamp(device.last_seen),
        "registered_at": timestamp(device.registered_at),
    }


def public_job(job: Job) -> dict[str, Any]:
    return {
        "id": job.id,
        "kind": job.kind,
        "model": job.model,
        "prompt": job.prompt,
        "status": job.status,
        "host_id": job.host_id,
        "target_host_id": job.target_host_id,
        "result": job.result,
        "error": job.error,
        "usage": job.usage,
        "note": job.note,
        "created_at": timestamp(job.created_at),
        "started_at": timestamp(job.started_at),
        "lease_updated_at": timestamp(job.lease_updated_at),
        "completed_at": timestamp(job.completed_at),
    }


async def publish_job(job: Job) -> None:
    try:
        await realtime.publish_job(job.id, {"job": public_job(job)})
    except Exception:
        pass


async def mark_present(host_id: str) -> None:
    try:
        await realtime.mark_present(host_id, settings.host_timeout_seconds)
    except Exception:
        pass


async def online_device_ids(devices: list[Device]) -> set[str]:
    ids = [device.id for device in devices]
    try:
        return await realtime.online_ids(ids)
    except Exception:
        cutoff = utcnow() - timedelta(seconds=settings.host_timeout_seconds)
        return {device.id for device in devices if device.last_seen >= cutoff}


async def authenticate_device(session: AsyncSession, host_id: str, token: str | None) -> Device:
    device = await session.get(Device, host_id)
    if not device or not token or not secrets.compare_digest(device.token_hash, hash_token(token)):
        raise HTTPException(status_code=401, detail="بيانات المضيف غير صحيحة")
    return device


async def requeue_host_jobs(session: AsyncSession, host_id: str, note: str) -> list[Job]:
    result = await session.execute(
        select(Job).where(
            or_(
                (Job.host_id == host_id) & (Job.status == "running"),
                (Job.target_host_id == host_id) & (Job.status.in_(["queued", "running"])),
            )
        )
    )
    jobs = list(result.scalars())
    for job in jobs:
        targeted = job.target_host_id == host_id
        job.status = "failed" if targeted else "queued"
        job.host_id = None
        job.started_at = None
        job.lease_updated_at = None
        job.error = "انقطع الجهاز المحدد أو أوقف المشاركة" if targeted else None
        job.completed_at = utcnow() if targeted else None
        job.note = "تعذر إكمال المهمة على الجهاز المحدد" if targeted else note
    return jobs


async def maintenance_loop() -> None:
    while True:
        await asyncio.sleep(10)
        cutoff = utcnow() - timedelta(seconds=settings.job_timeout_seconds)
        async with SessionLocal() as session:
            result = await session.execute(
                select(Job)
                .where(
                    or_(
                        (Job.status == "running") & (Job.lease_updated_at < cutoff),
                        (Job.status == "queued") & (Job.target_host_id.is_not(None)) & (Job.created_at < cutoff),
                    )
                )
                .with_for_update(skip_locked=True)
            )
            expired_jobs = list(result.scalars())
            hosts_to_release = {job.host_id or job.target_host_id for job in expired_jobs if job.host_id or job.target_host_id}
            for job in expired_jobs:
                targeted = job.target_host_id is not None
                job.status = "failed" if targeted else "queued"
                job.host_id = None
                job.started_at = None
                job.lease_updated_at = None
                job.error = "انتهت مهلة الجهاز المحدد" if targeted else None
                job.completed_at = utcnow() if targeted else None
                job.note = "تعذر إكمال المهمة على الجهاز المحدد" if targeted else "أعيدت المهمة للطابور بعد انقطاع المضيف"
            if hosts_to_release:
                hosts = await session.execute(select(Device).where(Device.id.in_(hosts_to_release)))
                for device in hosts.scalars():
                    device.busy = False
            await session.commit()
            for job in expired_jobs:
                await publish_job(job)


@asynccontextmanager
async def lifespan(_: FastAPI):
    if settings.auto_create_schema:
        await create_schema()
    await realtime.connect()
    maintenance_task = asyncio.create_task(maintenance_loop())
    try:
        yield
    finally:
        maintenance_task.cancel()
        with suppress(asyncio.CancelledError):
            await maintenance_task
        await realtime.close()


app = FastAPI(title="AI Palm Coordinator", version="0.7.0", lifespan=lifespan, docs_url=None, redoc_url=None)
if settings.allowed_host_list and settings.allowed_host_list != ["*"]:
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=settings.allowed_host_list)


@app.middleware("http")
async def security_headers(request: Request, call_next):
    try:
        content_length = int(request.headers.get("content-length") or 0)
    except ValueError:
        return JSONResponse({"error": "قيمة Content-Length غير صحيحة"}, status_code=400)
    if content_length > 128_000:
        return JSONResponse({"error": "الطلب أكبر من الحد المسموح"}, status_code=413)
    response = await call_next(request)
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["X-Frame-Options"] = "DENY"
    response.headers["Referrer-Policy"] = "same-origin"
    response.headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()"
    response.headers["Content-Security-Policy"] = (
        "default-src 'self'; img-src 'self' data:; style-src 'self'; "
        "script-src 'self' https://static.cloudflareinsights.com; "
        "connect-src 'self' https://cloudflareinsights.com; base-uri 'none'; frame-ancestors 'none'"
    )
    if not request.url.path.startswith("/api/"):
        response.headers["Cache-Control"] = "no-cache"
    return response


@app.exception_handler(HTTPException)
async def http_error(_: Request, exc: HTTPException):
    return JSONResponse({"error": exc.detail}, status_code=exc.status_code, headers=exc.headers)


@app.exception_handler(RequestValidationError)
async def validation_error(_: Request, exc: RequestValidationError):
    first = exc.errors()[0] if exc.errors() else {}
    return JSONResponse({"error": first.get("msg", "بيانات الطلب غير صحيحة")}, status_code=422)


@app.get("/api/status")
async def api_status(session: AsyncSession = Depends(get_session)):
    await session.execute(select(func.count()).select_from(Device))
    redis_ok = await realtime.healthy()
    return {"ok": redis_ok, "version": "0.7.0", "database": "ok", "realtime": "ok" if redis_ok else "unavailable"}


@app.get("/api/hosts")
async def list_hosts(session: AsyncSession = Depends(get_session)):
    result = await session.execute(select(Device).order_by(Device.name))
    devices = list(result.scalars())
    online_ids = await online_device_ids(devices)
    connected = [device for device in devices if device.id in online_ids and device.enabled]
    offline = [device for device in devices if device.id not in online_ids or not device.enabled]
    model_groups: dict[str, dict[str, Any]] = {}
    for device in connected:
        group = model_groups.setdefault(device.model, {"model": device.model, "connected": 0, "available": 0})
        group["connected"] += 1
        if not device.busy:
            group["available"] += 1
    models = sorted(model_groups.values(), key=lambda item: (-item["available"], item["model"].lower()))
    return {
        "hosts": [public_device(device) for device in connected],
        "offline_hosts": [public_device(device, online=False) for device in offline],
        "models": models,
        "stats": {
            "registered": len(devices),
            "connected": len(connected),
            "offline": len(devices) - len(connected),
        },
    }


@app.post("/api/hosts/register", status_code=201)
async def register_host(
    payload: HostRegistration,
    registration_key: Annotated[str | None, Header(alias="X-Registration-Key")] = None,
    session: AsyncSession = Depends(get_session),
):
    if not registration_key or not secrets.compare_digest(registration_key, settings.registration_key):
        raise HTTPException(status_code=403, detail="مفتاح ربط المنصة غير صحيح")
    host_id = payload.host_id if payload.host_id and HOST_ID_PATTERN.fullmatch(payload.host_id) else uuid.uuid4().hex
    token = secrets.token_urlsafe(32)
    device = await session.get(Device, host_id)
    if device is None:
        device = Device(
            id=host_id,
            token_hash=hash_token(token),
            name=payload.name,
            gpu=payload.gpu,
            gpu_vendor=payload.gpu_vendor,
            vram_gb=round(payload.vram_gb) if payload.vram_gb else None,
            mode=payload.mode,
            model=payload.model,
            capabilities=list(payload.capabilities),
            enabled=payload.enabled,
            busy=False,
            last_seen=utcnow(),
        )
        session.add(device)
    else:
        device.token_hash = hash_token(token)
        device.name = payload.name
        device.gpu = payload.gpu
        device.gpu_vendor = payload.gpu_vendor
        device.vram_gb = round(payload.vram_gb) if payload.vram_gb else None
        device.mode = payload.mode
        device.model = payload.model
        device.capabilities = list(payload.capabilities)
        device.enabled = payload.enabled
        active = await session.scalar(
            select(func.count()).select_from(Job).where(
                Job.status.in_(["queued", "running"]),
                or_(Job.host_id == device.id, Job.target_host_id == device.id),
            )
        )
        device.busy = bool(active)
        device.last_seen = utcnow()
    await session.commit()
    await mark_present(device.id)
    return {"host_id": device.id, "token": token}


@app.post("/api/hosts/{host_id}/heartbeat")
async def heartbeat(
    host_id: str,
    payload: Heartbeat,
    host_token: Annotated[str | None, Header(alias="X-Host-Token")] = None,
    session: AsyncSession = Depends(get_session),
):
    device = await authenticate_device(session, host_id, host_token)
    device.last_seen = utcnow()
    requeued: list[Job] = []
    if payload.enabled is not None:
        device.enabled = payload.enabled
        if not payload.enabled:
            device.busy = False
            requeued = await requeue_host_jobs(session, device.id, "أعيدت المهمة للطابور بعد إيقاف المضيف")
    if payload.enabled is not False:
        running = await session.execute(select(Job).where(Job.host_id == device.id, Job.status == "running"))
        for job in running.scalars():
            job.lease_updated_at = utcnow()
    await session.commit()
    if device.enabled:
        await mark_present(device.id)
    for job in requeued:
        await publish_job(job)
    return {"ok": True}


@app.get("/api/hosts/{host_id}/jobs/next")
async def next_job(
    host_id: str,
    host_token: Annotated[str | None, Header(alias="X-Host-Token")] = None,
    session: AsyncSession = Depends(get_session),
):
    device = await authenticate_device(session, host_id, host_token)
    device.last_seen = utcnow()
    await mark_present(device.id)
    if not device.enabled:
        await session.commit()
        return {"job": None}
    targeted_result = await session.execute(
        select(Job)
        .where(Job.status == "queued", Job.target_host_id == device.id)
        .order_by(Job.created_at)
        .limit(1)
        .with_for_update(skip_locked=True)
    )
    job = targeted_result.scalar_one_or_none()
    if not job and device.busy:
        await session.commit()
        return {"job": None}
    result = await session.execute(
        select(Job)
        .where(
            Job.status == "queued",
            Job.target_host_id.is_(None),
            or_(Job.model.is_(None), Job.model == device.model),
        )
        .order_by(Job.created_at)
        .limit(25)
        .with_for_update(skip_locked=True)
    )
    if not job:
        job = next((candidate for candidate in result.scalars() if candidate.kind in device.capabilities), None)
    if job:
        now = utcnow()
        job.status = "running"
        job.host_id = device.id
        job.started_at = now
        job.lease_updated_at = now
        job.note = "تُنفذ الآن على جهاز متطوع"
        device.busy = True
    await session.commit()
    if job:
        await publish_job(job)
    return {"job": public_job(job) if job else None}


@app.post("/api/jobs", status_code=201)
async def create_job(payload: JobCreate, request: Request, session: AsyncSession = Depends(get_session)):
    identity = request.client.host if request.client else "unknown"
    try:
        allowed = await realtime.allow_rate(f"jobs:{identity}", settings.public_jobs_per_minute)
    except Exception:
        allowed = True
    if not allowed:
        raise HTTPException(status_code=429, detail="تم تجاوز عدد الطلبات المسموح مؤقتًا")
    target: Device | None = None
    selected_model = payload.model or None
    if payload.target_host_id:
        if not HOST_ID_PATTERN.fullmatch(payload.target_host_id):
            raise HTTPException(status_code=422, detail="معرّف الجهاز المحدد غير صحيح")
        locked = await session.execute(
            select(Device).where(Device.id == payload.target_host_id).with_for_update()
        )
        target = locked.scalar_one_or_none()
        if (
            not target
            or not target.enabled
            or target.busy
            or payload.kind not in target.capabilities
            or (payload.model and target.model != payload.model)
        ):
            raise HTTPException(status_code=409, detail="الجهاز المحدد قيد الاستخدام أو لم يعد متاحًا")
        if target.id not in await online_device_ids([target]):
            raise HTTPException(status_code=503, detail="الجهاز المحدد غير متصل الآن")
        target.busy = True
        selected_model = target.model
    else:
        result = await session.execute(select(Device).where(Device.enabled.is_(True), Device.busy.is_(False)))
        candidates = [
            device
            for device in result.scalars()
            if payload.kind in device.capabilities and (not payload.model or device.model == payload.model)
        ]
        online_ids = await online_device_ids(candidates)
        if not any(device.id in online_ids for device in candidates):
            detail = "النموذج المحدد غير متاح حاليًا" if payload.model else "لا يوجد جهاز متاح لهذه المهمة حاليًا"
            raise HTTPException(status_code=503, detail=detail)
    job = Job(
        id=uuid.uuid4().hex[:16],
        kind=payload.kind,
        model=selected_model,
        target_host_id=target.id if target else None,
        prompt=payload.prompt,
        status="queued",
        result="",
        note="محجوز للجهاز المحدد" if target else "بانتظار جهاز متاح",
    )
    session.add(job)
    await session.commit()
    await publish_job(job)
    return {"job": public_job(job)}


@app.get("/api/jobs")
async def list_jobs(session: AsyncSession = Depends(get_session)):
    result = await session.execute(select(Job).order_by(Job.created_at.desc()).limit(25))
    return {"jobs": [public_job(job) for job in result.scalars()]}


@app.get("/api/jobs/{job_id}")
async def get_job(job_id: str, session: AsyncSession = Depends(get_session)):
    job = await session.get(Job, job_id)
    if not job:
        raise HTTPException(status_code=404, detail="المهمة غير موجودة")
    return {"job": public_job(job)}


@app.post("/api/jobs/{job_id}/stream")
async def stream_job(
    job_id: str,
    payload: JobStream,
    host_token: Annotated[str | None, Header(alias="X-Host-Token")] = None,
    session: AsyncSession = Depends(get_session),
):
    job = await session.get(Job, job_id)
    if not job or not job.host_id or job.status != "running":
        raise HTTPException(status_code=401, detail="المهمة أو بيانات المضيف غير صحيحة")
    await authenticate_device(session, job.host_id, host_token)
    if payload.delta:
        job.result = (job.result + payload.delta)[: settings.max_result_chars]
        job.note = "تصل الإجابة الآن"
    if payload.usage is not None:
        job.usage = payload.usage
    job.lease_updated_at = utcnow()
    await session.commit()
    await publish_job(job)
    return {"ok": True}


@app.post("/api/jobs/{job_id}/complete")
async def complete_job(
    job_id: str,
    payload: JobComplete,
    host_token: Annotated[str | None, Header(alias="X-Host-Token")] = None,
    session: AsyncSession = Depends(get_session),
):
    job = await session.get(Job, job_id)
    if not job or not job.host_id:
        raise HTTPException(status_code=401, detail="المهمة أو بيانات المضيف غير صحيحة")
    device = await authenticate_device(session, job.host_id, host_token)
    job.status = "completed"
    job.result = (payload.result or job.result)[: settings.max_result_chars]
    job.usage = payload.usage or job.usage
    job.note = "اكتملت المهمة بنجاح"
    job.completed_at = utcnow()
    device.busy = False
    await session.commit()
    await publish_job(job)
    return {"ok": True}


@app.post("/api/jobs/{job_id}/fail")
async def fail_job(
    job_id: str,
    payload: JobFail,
    host_token: Annotated[str | None, Header(alias="X-Host-Token")] = None,
    session: AsyncSession = Depends(get_session),
):
    job = await session.get(Job, job_id)
    if not job or not job.host_id:
        raise HTTPException(status_code=401, detail="المهمة أو بيانات المضيف غير صحيحة")
    device = await authenticate_device(session, job.host_id, host_token)
    job.status = "failed"
    job.error = payload.error
    job.note = "تعذر إكمال المهمة"
    job.completed_at = utcnow()
    device.busy = False
    await session.commit()
    await publish_job(job)
    return {"ok": True}


@app.get("/api/jobs/{job_id}/events")
async def job_events(job_id: str):
    async with SessionLocal() as session:
        job = await session.get(Job, job_id)
        if not job:
            raise HTTPException(status_code=404, detail="المهمة غير موجودة")

    async def events():
        async with SessionLocal() as initial_session:
            initial = await initial_session.get(Job, job_id)
            if initial:
                yield f"data: {json.dumps({'job': public_job(initial)}, ensure_ascii=False)}\n\n"
                if initial.status in FINAL_JOB_STATES:
                    return
        async for message in realtime.subscribe_job(job_id):
            if message is None:
                async with SessionLocal() as check_session:
                    current = await check_session.get(Job, job_id)
                    if current and current.status in FINAL_JOB_STATES:
                        yield f"data: {json.dumps({'job': public_job(current)}, ensure_ascii=False)}\n\n"
                        return
                yield ": keep-alive\n\n"
                continue
            yield f"data: {message}\n\n"
            try:
                status = json.loads(message).get("job", {}).get("status")
            except json.JSONDecodeError:
                status = None
            if status in FINAL_JOB_STATES:
                return

    return StreamingResponse(
        events(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


if not settings.web_root.is_dir():
    raise RuntimeError(f"Web directory not found: {settings.web_root}")
app.mount("/", StaticFiles(directory=settings.web_root, html=True), name="web")
