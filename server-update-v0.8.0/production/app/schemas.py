from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class HostRegistration(StrictModel):
    host_id: str | None = Field(default=None, max_length=64)
    name: str = Field(default="جهاز متطوع", min_length=1, max_length=80)
    gpu: str = Field(default="بطاقة غير معروفة", min_length=1, max_length=120)
    gpu_vendor: str = Field(default="غير معروف", max_length=40)
    vram_gb: float | None = Field(default=None, gt=0, le=1024)
    context_length: int | None = Field(default=None, gt=0, le=10_000_000)
    mode: str = Field(default="openai", max_length=30)
    model: str = Field(min_length=1, max_length=120)
    capabilities: list[Literal["chat", "image"]] = Field(default_factory=lambda: ["chat"], min_length=1, max_length=2)
    enabled: bool = True

    @field_validator("capabilities")
    @classmethod
    def unique_capabilities(cls, value: list[str]) -> list[str]:
        return list(dict.fromkeys(value))


class Heartbeat(StrictModel):
    enabled: bool | None = None


class ChatMessage(StrictModel):
    role: Literal["system", "user", "assistant"]
    content: str = Field(min_length=1, max_length=100_000)


class JobCreate(StrictModel):
    kind: Literal["chat", "image"] = "chat"
    prompt: str = Field(min_length=1, max_length=2000)
    model: str | None = Field(default=None, max_length=120)
    target_host_id: str | None = Field(default=None, max_length=64)
    messages: list[ChatMessage] | None = Field(default=None, min_length=1, max_length=128)

    @field_validator("prompt", "model", "target_host_id")
    @classmethod
    def strip_text(cls, value: str | None) -> str | None:
        return value.strip() if isinstance(value, str) else value

    @model_validator(mode="after")
    def validate_messages(self):
        if self.messages and sum(len(item.content) for item in self.messages) > 1_000_000:
            raise ValueError("conversation history is too large")
        return self


class JobStream(StrictModel):
    delta: str = Field(default="", max_length=20_000)
    thinking_delta: str = Field(default="", max_length=20_000)
    usage: dict[str, Any] | None = None


class JobComplete(StrictModel):
    result: str = Field(default="", max_length=1_000_000)
    usage: dict[str, Any] | None = None


class JobFail(StrictModel):
    error: str = Field(default="فشل التنفيذ", max_length=1000)
