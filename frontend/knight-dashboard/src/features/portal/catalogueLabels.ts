// Persian display names for the commercial catalogue, keyed by the stable slug
// (features) and key (plans) the API returns.
//
// The catalogue seed carries English names — fine for the operator dashboard, but
// the merchant-facing portal is Persian-first, so we localise for display here
// without touching the seed or the API. Anything without a Persian entry falls
// back to the English name/description the API sent, so a newly added feature is
// never blank — just untranslated until a line is added below.

interface Label {
  name: string;
  description?: string;
}

/** Feature slug → Persian label. */
const FEATURE_LABELS: Record<string, Label> = {
  storefront: {
    name: "ویترین فروشگاه",
    description: "بخش پیش‌روی مشتری: مرور محصولات، صفحهٔ کالا، سبد خرید و مسیر تسویه‌حساب.",
  },
  catalog: {
    name: "کاتالوگ محصولات",
    description: "محصولات یا آیتم‌های منو، دسته‌ها، تنوع‌ها، قیمت‌ها و موجود بودن.",
  },
  accounts: {
    name: "حساب مشتریان",
    description: "هویت و ورود مشتری، خرید مهمان و دفترچهٔ آدرس.",
  },
  orders: {
    name: "سفارش‌ها و تسویه‌حساب",
    description: "خودِ تراکنش: جمع سبد، تسویه، چرخهٔ عمر سفارش، لغو و وضعیت بازپرداخت.",
  },
  "order-management": {
    name: "مدیریت سفارش",
    description: "دیدن و پیگیری سفارش‌ها، تغییر وضعیت و رسیدگی به آن‌ها.",
  },
  payments: {
    name: "پرداخت‌ها",
    description: "ثبت دریافت وجه روی یک لایهٔ انتزاعیِ درگاه. یکپارچه‌سازی درگاه‌ها جداست.",
  },
  shipping: {
    name: "ارسال و تحویل",
    description: "روش‌های ارسال و دریافت حضوری، مناطق و هزینه‌های ارسال و وضعیت آماده‌سازی.",
  },
  promotions: {
    name: "کدهای تخفیف",
    description: "کدهای تخفیف درصدی و مبلغی، بازهٔ اعتبار، حداقل مبلغ سفارش و محدودیت محصول.",
  },
  "analytics-core": {
    name: "تحلیل پایه",
    description: "درآمد، سفارش‌ها، میانگین ارزش سفارش و پرفروش‌ها در یک بازهٔ زمانی؛ بنیانِ داده‌ای بقیهٔ تحلیل‌ها.",
  },
  "analytics-reports": {
    name: "گزارش‌های تحلیلی",
    description: "لایهٔ گزارش روی داده‌های تحلیلی: خلاصه‌های روزانه و ارقام قابل‌خروجی‌گرفتن.",
  },
  "log-shipping": {
    name: "ارسال لاگ‌ها",
    description: "فروشگاه لاگ‌های ساخت‌یافته‌اش را به نایت می‌فرستد تا جستجوپذیر و نگهداری شوند.",
  },
  "advanced-promotions": {
    name: "تخفیف‌های پیشرفته",
    description: "بخر X بگیر Y، بسته‌ها، کمپین‌های دسته‌ای و مشتری‌محور، زمان‌بندی و قواعد ترکیب.",
  },
  "loyalty-rewards": {
    name: "باشگاه مشتریان و پاداش",
    description: "امتیاز، سطوح، قواعد کسب و مصرف امتیاز و دفترچهٔ امتیاز هر مشتری.",
  },
  "customer-segmentation": {
    name: "بخش‌بندی مشتریان",
    description: "بخش‌های VIP، جدید، غیرفعال و پرارزش که زمان‌بندی‌شده از تاریخچهٔ خرید بازمحاسبه می‌شوند.",
  },
  "gift-cards": {
    name: "کارت هدیه و اعتبار فروشگاه",
    description: "کارت هدیه‌ای که مشتری می‌خرد، هدیه می‌دهد و خرج می‌کند، و اعتبار قابل‌مصرف در سفارش‌ها.",
  },
  "marketing-automation": {
    name: "بازاریابی خودکار",
    description: "کمپین‌های سبد رهاشده، خوش‌آمد، پس از خرید و بازگرداندن مشتری روی بخش‌های مشتریان.",
  },
  "ai-reports": {
    name: "گزارش‌های هوش مصنوعی",
    description: "تفسیر خودکار داده‌های تحلیلی: چه چیزی تغییر کرد، چه چیزی غیرعادی است و چه باید کرد.",
  },
  "ai-recommendations": {
    name: "پیشنهاد هوشمند محصول",
    description: "«مشتریانی که این را خریدند، این را هم خریدند» — پیشنهادگری آموخته از سبدهای خرید واقعی با پشتیبانِ پرفروش‌ها.",
  },
  "advanced-inventory": {
    name: "انبارداری پیشرفته",
    description: "حرکت موجودی، رزرو، انبارها، هشدار کمبود، سفارش خرید و تأمین‌کننده‌ها.",
  },
  "restaurant-operations": {
    name: "عملیات رستوران",
    description: "میزها، وضعیت‌های آشپزخانه و نمایش سفارش، زمان آماده‌سازی، مدیریت بار و زمان‌بندی تحویل.",
  },
  "multi-location": {
    name: "چند شعبه",
    description: "یک کسب‌وکار با چند شعبه یا انبار: موجودی، منو، کارکنان و سفارش‌ها به تفکیک شعبه.",
  },
  subscriptions: {
    name: "اشتراک و سفارش تکرارشونده",
    description: "فروش اشتراکی و سفارش‌هایی که به‌صورت دوره‌ای تکرار می‌شوند.",
  },
  "external-marketplaces": {
    name: "اتصال به مارکت‌پلیس‌ها و پیک",
    description: "یکپارچه‌سازی با بازارها و سرویس‌های تحویل بیرونی.",
  },
  "auto-admin": {
    name: "ادمین خودکار",
    description: "دستیارِ خودکارِ تولید و انتشار محتوا در کانال‌های فروشگاه.",
  },
  "auto-admin-image": { name: "ادمین خودکار — ساخت تصویر" },
  "auto-admin-caption": { name: "ادمین خودکار — نگارش کپشن" },
  "auto-admin-story": { name: "ادمین خودکار — استوری" },
  "auto-admin-video": { name: "ادمین خودکار — ویدیوی کوتاه" },
  "auto-admin-telegram": { name: "ادمین خودکار — انتشار در تلگرام" },
  "auto-admin-instagram": { name: "ادمین خودکار — انتشار در اینستاگرام" },
  "auto-admin-divar": { name: "ادمین خودکار — انتشار در دیوار" },
  "auto-admin-basalam": { name: "ادمین خودکار — انتشار در باسلام" },
  "auto-admin-autoreply": { name: "ادمین خودکار — پاسخ خودکار" },
  "auto-admin-boost": { name: "ادمین خودکار — افزایش دیده‌شدن" },
  "auto-admin-autopilot": { name: "ادمین خودکار — خلبان خودکار" },
};

/** Plan key → Persian label. */
const PLAN_LABELS: Record<string, Label> = {
  basic: {
    name: "پایه",
    description: "یک فروشگاه کارآمد: ویترین و مدیریت سفارش، روی میزبانی اشتراکی.",
  },
  custom: {
    name: "دلخواه",
    description: "پلن پایه به‌علاوهٔ قابلیت‌های اختیاری‌ای که خودتان انتخاب می‌کنید.",
  },
  professional: {
    name: "حرفه‌ای",
    description: "همه‌چیز، به‌همراه قابلیت‌های میزبانی اختصاصی.",
  },
  growth: {
    name: "رشد",
    description: "فروشگاه پایه به‌علاوهٔ هر آنچه به فروش بیشتر کمک می‌کند: تحلیل، چیدمان پیشرفته، نظرات و بخش‌بندی.",
  },
  retention: {
    name: "نگه‌داشت",
    description: "فروشگاه پایه به‌علاوهٔ هر آنچه مشتری را بازمی‌گرداند: باشگاه مشتریان، کارت هدیه، بخش‌بندی و تحلیل زیربنایی‌شان.",
  },
};

/** Persian name for a feature, falling back to the API's name. */
export function featureName(slug: string, fallback: string): string {
  return FEATURE_LABELS[slug]?.name ?? fallback;
}

/** Persian description for a feature, falling back to the API's description. */
export function featureDescription(slug: string, fallback: string | null): string | null {
  return FEATURE_LABELS[slug]?.description ?? fallback;
}

/** Persian name for a plan, falling back to the API's name. */
export function planName(key: string, fallback: string): string {
  return PLAN_LABELS[key]?.name ?? fallback;
}

/** Persian description for a plan, falling back to the API's description. */
export function planDescription(key: string, fallback: string | null): string | null {
  return PLAN_LABELS[key]?.description ?? fallback;
}

/** The English plan names the API sends, mapped to their key, so a surface that
 *  only has the display name (e.g. the active subscription) can still localise. */
const PLAN_KEY_BY_ENGLISH_NAME: Record<string, string> = {
  Basic: "basic",
  Custom: "custom",
  Professional: "professional",
  Growth: "growth",
  Retention: "retention",
};

/** Persian plan name given only the API's (English) display name. */
export function planNameFromDisplay(displayName: string): string {
  const key = PLAN_KEY_BY_ENGLISH_NAME[displayName];
  return key ? (PLAN_LABELS[key]?.name ?? displayName) : displayName;
}
