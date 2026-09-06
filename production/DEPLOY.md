# نشر نخلة AI على نطاق حقيقي وVPS

هذه الحزمة تشغّل أربعة أجزاء معًا: موقع وخادم FastAPI، قاعدة PostgreSQL، Redis للتحديث اللحظي، وCaddy لإصدار وتجديد شهادة HTTPS تلقائيًا. لا يُفتح للعامة سوى المنفذين 80 و443، ولا يحتاج خادم النموذج المحلي على جهاز المتطوع إلى أي منفذ وارد.

## المتطلبات

- VPS بنظام Ubuntu LTS حديث، بذاكرة 2 GB على الأقل للتجربة.
- نطاق فرعي مثل `nawah.example.com` وسجل DNS من نوع A يشير إلى عنوان الـVPS.
- Docker Engine مع إضافة Docker Compose.
- السماح بالمنافذ 22 و80 و443 في جدار الحماية.

## 1. إعداد الأسرار محليًا

من مجلد `production` انسخ ملف المثال:

```bash
cp .env.example .env
```

أنشئ ثلاث قيم مختلفة:

```bash
openssl rand -hex 32
openssl rand -hex 32
openssl rand -hex 32
```

افتح `.env` واستبدل القيم التجريبية بالقيم الناتجة، ثم استبدل `nawah.example.com` بنطاقك الحقيقي في الحقول الثلاثة. يجب أن تتطابق كلمة مرور PostgreSQL داخل `POSTGRES_PASSWORD` ومع الجزء الموجود داخل `NAWAH_DATABASE_URL`، وكذلك كلمة Redis.

لا ترفع ملف `.env` إلى مستودع عام ولا ترسله لأحد. قيمة `NAWAH_REGISTRATION_KEY` هي مفتاح ربط أجهزة النسخة التجريبية.

## 2. رفع المشروع وتشغيله

ارفع مجلد المشروع كاملًا إلى الخادم، ثم ادخل إلى مجلد `production` وشغّل:

```bash
docker compose up -d --build
```

في أول تشغيل تُنشأ جداول قاعدة البيانات بترحيل رسمي، ثم يبدأ الموقع. Caddy يطلب شهادة HTTPS للنطاق تلقائيًا بعد أن يصبح سجل DNS صحيحًا.

## 3. التحقق

```bash
docker compose ps
curl https://nawah.example.com/api/status
docker compose logs --tail=100 api caddy
```

غيّر النطاق في أمر `curl`. النتيجة الصحيحة تتضمن `"ok":true`. بعد ذلك افتح النطاق في المتصفح.

## 4. ربط تطبيق Windows

افتح `AIPalmHost.exe` واكتب:

- رابط منصة نخلة AI: `https://nawah.example.com`
- مفتاح ربط المنصة: قيمة `NAWAH_REGISTRATION_KEY`
- عنوان OpenAI-compatible API المحلي ومفتاحه إن وُجد.
- اختر النموذج واضغط «اتصال وبدء المشاركة».

يُحفظ مفتاح ربط المنصة مشفرًا لحساب Windows الحالي. أما مفتاح خادم النموذج فلا يُحفظ. جميع اتصالات التطبيق صادرة، لذلك لا تفتح منفذ LM Studio أو Ollama أو غيرهما في الراوتر أو جدار الحماية.

## التحديث والنسخ الاحتياطي

بعد رفع إصدار جديد:

```bash
docker compose up -d --build
```

### تحديث نسخة aaPanel الحالية

ارفع الملف `dist/ai-palm-aapanel.zip` إلى `/www/wwwroot/nawah` ثم فك الضغط مع السماح باستبدال مجلدي `production` و`web`. لا تحذف ملف `.env` الموجود على الخادم. بعد ذلك افتح مشروع Docker Compose في aaPanel واضغط **Save** ثم **Confirm** ليعاد بناء حاوية `api` فقط مع الواجهة الجديدة.

لا يحتاج مشروع الـReverse Proxy أو Cloudflare أو الشهادة إلى أي تغيير. تحقق بعد التحديث من:

- `https://nawah.almshary.site/` للصفحة الرئيسية.
- `https://nawah.almshary.site/chat/` للمحادثة.
- `https://nawah.almshary.site/downloads/AIPalmHost.exe` لتنزيل التطبيق.
- `https://nawah.almshary.site/api/status` ويجب أن يعرض الإصدار `0.7.0`.

لإنشاء نسخة احتياطية من قاعدة البيانات:

```bash
mkdir -p backups
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' | gzip > "backups/nawah-$(date +%F-%H%M).sql.gz"
```

لتتبع مشكلة دون إيقاف الخدمات:

```bash
docker compose logs -f --tail=200 api caddy postgres redis
```

## قبل فتح المنصة للجمهور الواسع

مفتاح التسجيل المشترك مناسب لاختبار مغلق فقط. قبل الانتشار العام نضيف حسابات للمضيفين، اقتران جهاز لمرة واحدة، فلترة محتوى، سقف استخدام لكل زائر، صفحة سياسة وخصوصية، ومراقبة للمهام والأعطال. البنية الحالية تهيئ هذه المرحلة لكنها لا تدّعي أنها نظام إنتاج جماهيري مكتمل بعد.
