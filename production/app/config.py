from __future__ import annotations

from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


PROJECT_ROOT = Path(__file__).resolve().parents[2]


class Settings(BaseSettings):
    environment: str = "development"
    database_url: str = "sqlite+aiosqlite:///./nawah-dev.db"
    redis_url: str = "memory://"
    registration_key: str = "local-development-only"
    allowed_hosts: str = "127.0.0.1,localhost,testserver"
    public_base_url: str = "http://127.0.0.1:8000"
    web_root: Path = PROJECT_ROOT / "web"
    host_timeout_seconds: int = 35
    job_timeout_seconds: int = 90
    public_jobs_per_minute: int = 12
    auto_create_schema: bool = True
    max_result_chars: int = 100_000

    model_config = SettingsConfigDict(
        env_prefix="NAWAH_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    @property
    def allowed_host_list(self) -> list[str]:
        return [value.strip() for value in self.allowed_hosts.split(",") if value.strip()]


@lru_cache
def get_settings() -> Settings:
    return Settings()
