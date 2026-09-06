from __future__ import annotations

import asyncio
import json
import time
from collections import defaultdict
from collections.abc import AsyncIterator
from typing import Any

from redis.asyncio import Redis


class RealtimeStore:
    def __init__(self, url: str, prefix: str = "nawah") -> None:
        self.url = url
        self.prefix = prefix
        self.redis: Redis | None = None
        self.memory_presence: dict[str, float] = {}
        self.memory_rates: dict[str, tuple[int, float]] = {}
        self.memory_subscribers: dict[str, set[asyncio.Queue[str]]] = defaultdict(set)

    @property
    def memory_mode(self) -> bool:
        return self.url.startswith("memory://")

    async def connect(self) -> None:
        if self.memory_mode:
            return
        self.redis = Redis.from_url(self.url, encoding="utf-8", decode_responses=True)
        await self.redis.ping()

    async def close(self) -> None:
        if self.redis is not None:
            await self.redis.aclose()

    async def healthy(self) -> bool:
        if self.memory_mode:
            return True
        try:
            return bool(self.redis and await self.redis.ping())
        except Exception:
            return False

    async def mark_present(self, host_id: str, ttl_seconds: int) -> None:
        if self.memory_mode:
            self.memory_presence[host_id] = time.monotonic() + ttl_seconds
            return
        if self.redis is None:
            raise RuntimeError("Redis is not connected")
        await self.redis.set(f"{self.prefix}:presence:{host_id}", "1", ex=ttl_seconds)

    async def online_ids(self, host_ids: list[str]) -> set[str]:
        if not host_ids:
            return set()
        if self.memory_mode:
            now = time.monotonic()
            return {host_id for host_id in host_ids if self.memory_presence.get(host_id, 0) > now}
        if self.redis is None:
            raise RuntimeError("Redis is not connected")
        values = await self.redis.mget([f"{self.prefix}:presence:{host_id}" for host_id in host_ids])
        return {host_id for host_id, value in zip(host_ids, values, strict=True) if value is not None}

    async def publish_job(self, job_id: str, payload: dict[str, Any]) -> None:
        message = json.dumps(payload, ensure_ascii=False)
        channel = f"{self.prefix}:job:{job_id}"
        if self.memory_mode:
            for queue in tuple(self.memory_subscribers[channel]):
                queue.put_nowait(message)
            return
        if self.redis is None:
            raise RuntimeError("Redis is not connected")
        await self.redis.publish(channel, message)

    async def subscribe_job(self, job_id: str) -> AsyncIterator[str | None]:
        channel = f"{self.prefix}:job:{job_id}"
        if self.memory_mode:
            queue: asyncio.Queue[str] = asyncio.Queue(maxsize=100)
            self.memory_subscribers[channel].add(queue)
            try:
                while True:
                    try:
                        yield await asyncio.wait_for(queue.get(), timeout=15)
                    except TimeoutError:
                        yield None
            finally:
                self.memory_subscribers[channel].discard(queue)
            return
        if self.redis is None:
            raise RuntimeError("Redis is not connected")
        async with self.redis.pubsub() as pubsub:
            await pubsub.subscribe(channel)
            while True:
                message = await pubsub.get_message(ignore_subscribe_messages=True, timeout=15)
                yield str(message["data"]) if message else None

    async def allow_rate(self, identity: str, limit: int, window_seconds: int = 60) -> bool:
        if limit <= 0:
            return True
        bucket = int(time.time() // window_seconds)
        key = f"{self.prefix}:rate:{identity}:{bucket}"
        if self.memory_mode:
            count, expires_at = self.memory_rates.get(key, (0, time.monotonic() + window_seconds))
            if expires_at <= time.monotonic():
                count, expires_at = 0, time.monotonic() + window_seconds
            count += 1
            self.memory_rates[key] = (count, expires_at)
            return count <= limit
        if self.redis is None:
            raise RuntimeError("Redis is not connected")
        async with self.redis.pipeline(transaction=True) as pipeline:
            pipeline.incr(key)
            pipeline.expire(key, window_seconds + 2)
            count, _ = await pipeline.execute()
        return int(count) <= limit
