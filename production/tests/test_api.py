from __future__ import annotations

import asyncio
import os
from pathlib import Path


TEST_DB = Path(__file__).resolve().parent / "nawah-test.db"
TEST_DB.unlink(missing_ok=True)
os.environ["NAWAH_DATABASE_URL"] = f"sqlite+aiosqlite:///{TEST_DB.as_posix()}"
os.environ["NAWAH_REDIS_URL"] = "memory://"
os.environ["NAWAH_REGISTRATION_KEY"] = "test-link-key"
os.environ["NAWAH_ALLOWED_HOSTS"] = "testserver,localhost,127.0.0.1"
os.environ["NAWAH_AUTO_CREATE_SCHEMA"] = "true"
os.environ["NAWAH_HOST_TIMEOUT_SECONDS"] = "60"

from fastapi.testclient import TestClient  # noqa: E402

from production.app.database import engine  # noqa: E402
from production.app.main import app  # noqa: E402


def register(client: TestClient, host_id: str, model: str) -> dict:
    response = client.post(
        "/api/hosts/register",
        headers={"X-Registration-Key": "test-link-key"},
        json={
            "host_id": host_id,
            "name": f"device-{model}",
            "gpu": "Radeon RX 9070 XT",
            "gpu_vendor": "AMD",
            "vram_gb": 15.9,
            "context_length": 131072,
            "mode": "openai",
            "model": model,
            "capabilities": ["chat"],
            "enabled": True,
        },
    )
    assert response.status_code == 201, response.text
    return response.json()


def test_end_to_end_model_routing_and_stable_device_id() -> None:
    with TestClient(app) as client:
        assert client.get("/api/status").json()["ok"] is True
        denied = client.post("/api/hosts/register", json={"model": "denied"})
        assert denied.status_code == 403

        alpha = register(client, "stable-alpha-0001", "model-alpha")
        alpha_again = register(client, "stable-alpha-0001", "model-alpha")
        beta = register(client, "stable-beta-00002", "model-beta")
        assert alpha["host_id"] == alpha_again["host_id"]

        hosts = client.get("/api/hosts").json()
        assert hosts["stats"] == {"registered": 2, "connected": 2, "offline": 0}
        assert hosts["offline_hosts"] == []
        assert {item["model"] for item in hosts["models"]} == {"model-alpha", "model-beta"}
        assert all(device["vram_gb"] == 16 for device in hosts["hosts"])
        assert all(device["context_length"] == 131072 for device in hosts["hosts"])

        created = client.post(
            "/api/jobs",
            json={
                "kind": "chat",
                "model": "model-beta",
                "prompt": "اكتب جوابًا تجريبيًا",
                "messages": [
                    {"role": "user", "content": "مرحباً"},
                    {"role": "assistant", "content": "أهلاً بك"},
                    {"role": "user", "content": "اكتب جوابًا تجريبيًا"},
                ],
            },
        )
        assert created.status_code == 201, created.text
        job_id = created.json()["job"]["id"]

        wrong_host = client.get(
            "/api/hosts/stable-alpha-0001/jobs/next",
            headers={"X-Host-Token": alpha_again["token"]},
        )
        assert wrong_host.json()["job"] is None

        assigned = client.get(
            "/api/hosts/stable-beta-00002/jobs/next",
            headers={"X-Host-Token": beta["token"]},
        )
        assert assigned.json()["job"]["id"] == job_id
        assert len(assigned.json()["job"]["messages"]) == 3

        streamed = client.post(
            f"/api/jobs/{job_id}/stream",
            headers={"X-Host-Token": beta["token"]},
            json={"thinking_delta": "أحلل السؤال...", "delta": "مرحباً ", "usage": {"completion_tokens": 1}},
        )
        assert streamed.status_code == 200
        completed = client.post(
            f"/api/jobs/{job_id}/complete",
            headers={"X-Host-Token": beta["token"]},
            # The desktop host already streamed the answer. Completion must not
            # resend a potentially very large result body.
            json={"result": "", "usage": {"prompt_tokens": 4, "completion_tokens": 3}},
        )
        assert completed.status_code == 200

        job = client.get(f"/api/jobs/{job_id}").json()["job"]
        assert job["status"] == "completed"
        assert job["result"] == "مرحباً "
        assert job["thinking"] == "أحلل السؤال..."
        assert job["usage"]["completion_tokens"] == 3

        targeted = client.post(
            "/api/jobs",
            json={
                "kind": "chat",
                "model": "model-alpha",
                "target_host_id": "stable-alpha-0001",
                "prompt": "نفّذ هذه المهمة على جهاز ألفا تحديداً",
            },
        )
        assert targeted.status_code == 201, targeted.text
        targeted_job = targeted.json()["job"]
        assert targeted_job["target_host_id"] == "stable-alpha-0001"
        assert targeted_job["host_id"] is None

        hosts = client.get("/api/hosts").json()
        alpha_host = next(item for item in hosts["hosts"] if item["id"] == "stable-alpha-0001")
        alpha_model = next(item for item in hosts["models"] if item["model"] == "model-alpha")
        assert alpha_host["busy"] is True
        assert alpha_model["available"] == 0

        duplicate = client.post(
            "/api/jobs",
            json={
                "kind": "chat",
                "model": "model-alpha",
                "target_host_id": "stable-alpha-0001",
                "prompt": "يجب ألا تُقبل مهمة ثانية",
            },
        )
        assert duplicate.status_code == 409

        other_host = client.get(
            "/api/hosts/stable-beta-00002/jobs/next",
            headers={"X-Host-Token": beta["token"]},
        )
        assert other_host.json()["job"] is None
        exact_host = client.get(
            "/api/hosts/stable-alpha-0001/jobs/next",
            headers={"X-Host-Token": alpha_again["token"]},
        )
        assert exact_host.json()["job"]["id"] == targeted_job["id"]
        assert exact_host.json()["job"]["host_id"] == "stable-alpha-0001"

        targeted_done = client.post(
            f"/api/jobs/{targeted_job['id']}/complete",
            headers={"X-Host-Token": alpha_again["token"]},
            json={"result": "تمت المهمة المحددة"},
        )
        assert targeted_done.status_code == 200
        hosts = client.get("/api/hosts").json()
        alpha_host = next(item for item in hosts["hosts"] if item["id"] == "stable-alpha-0001")
        assert alpha_host["busy"] is False

        disabled = client.post(
            "/api/hosts/stable-beta-00002/heartbeat",
            headers={"X-Host-Token": beta["token"]},
            json={"enabled": False},
        )
        assert disabled.status_code == 200
        hosts = client.get("/api/hosts").json()
        assert hosts["stats"] == {"registered": 2, "connected": 1, "offline": 1}
        assert len(hosts["hosts"]) == 1
        assert len(hosts["offline_hosts"]) == 1
        assert hosts["offline_hosts"][0]["id"] == "stable-beta-00002"
        assert hosts["offline_hosts"][0]["online"] is False


def teardown_module() -> None:
    asyncio.run(engine.dispose())
    TEST_DB.unlink(missing_ok=True)
