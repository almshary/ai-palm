AI Palm server update 0.8.0

This update adds:
- Device context-length registration.
- Multi-turn chat messages.
- Database migration 0004_context_messages.
- Larger streamed-result allowance for long code responses.
- Required predecessor migration 0003_job_thinking is included for servers
  whose previous partial update did not retain that migration file.

Replace the included files in the matching production paths, then rebuild/restart
the Compose project. The existing Compose startup command runs `alembic upgrade head`
automatically before starting the API.

تحديث خادم نخلة AI رقم 0.8.0

استبدل الملفات في مسارات production المطابقة، ثم أعد بناء وتشغيل مشروع Compose.
أمر التشغيل الحالي ينفذ ترقية قاعدة البيانات تلقائياً قبل تشغيل API.
