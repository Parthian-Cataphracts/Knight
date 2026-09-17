// Plain-language, Persian in-product help for every KNIGHT area.
//
// The same content the repo guide carries (docs/usage/knight-control-plane.md),
// keyed by route path so a help button can show the current area's guide and the
// dedicated /help page can list them all. Keep this in step with that doc.

export interface HelpEntry {
  /** Route path this help belongs to. */
  path: string;
  /** Persian title shown in the drawer and the index. */
  title: string;
  /** One simple sentence: what this is. */
  what: string;
  /** Step-by-step, what the operator does here. */
  steps: string[];
  /** Optional caution for irreversible actions. */
  caution?: string;
}

export const HELP_SECTIONS: { group: string; entries: HelpEntry[] }[] = [
  {
    group: "عملیات",
    entries: [
      {
        path: "/",
        title: "داشبورد",
        what: "صفحهٔ اول؛ خلاصهٔ کلی وضعیت سکو (تعداد فروشگاه‌ها، مشتری‌ها، وضعیت اخیر).",
        steps: ["فقط نگاه کنید تا یک تصویر کلی بگیرید، بعد سراغ بخش موردنظر بروید."],
      },
      {
        path: "/customers",
        title: "مشتریان",
        what: "«مشتری» یعنی صاحبِ یک کسب‌وکار که از سکو استفاده می‌کند (نه خریدارِ فروشگاه). هر مشتری می‌تواند چند فروشگاه داشته باشد.",
        steps: [
          "روی هر مشتری بزنید تا جزئیات، فروشگاه‌ها، اشتراک و فیچرهای فعالش را ببینید.",
          "«مشتری جدید» یک کسب‌وکار تازه می‌سازد (نام، ایمیل ادمین، پلن).",
          "در جزئیات مشتری می‌توانید اجازهٔ استفاده از یک فیچر (entitlement) را بدهید یا بگیرید.",
        ],
      },
      {
        path: "/stores",
        title: "فروشگاه‌ها",
        what: "فهرست فروشگاه‌های واقعی که ساخته شده‌اند (مثل «بوژان»).",
        steps: ["روی هر فروشگاه بزنید تا وضعیت اتصال، فیچرهای نصب‌شده، سلامت و جزئیاتش را ببینید."],
      },
      {
        path: "/provisioning",
        title: "آماده‌سازی/تدارک",
        what: "جایی که یک فروشگاه جدید از صفر برپا می‌شود (سرور، دیتابیس، آدرس).",
        steps: ["درخواست ساخت زیرساخت فروشگاه را دنبال کنید و وضعیت مراحلش را ببینید."],
      },
    ],
  },
  {
    group: "سرویس",
    entries: [
      {
        path: "/features",
        title: "فیچرها",
        what: "کاتالوگ فیچرها و نسخه‌های منتشرشده‌شان.",
        steps: [
          "فهرست فیچرها و وضعیت‌شان (منتشرشده/برداشته‌شده) را ببینید.",
          "نسخهٔ جدید یک فیچر این‌جا منتشر می‌شود؛ ابزار deliver_service.sh هم همین را خودکار می‌کند.",
        ],
      },
      {
        path: "/store-images",
        title: "تصاویر فروشگاه",
        what: "ایمیج‌های آمادهٔ اجرا برای فروشگاه‌ها/سرویس‌ها.",
        steps: ["نسخه‌های ایمیج را ببینید و مدیریت کنید."],
      },
      {
        path: "/rollouts",
        title: "انتشار تدریجی",
        what: "پخش پله‌ای یک نسخهٔ فیچر روی فروشگاه‌ها (به‌جای همه با هم).",
        steps: ["یک rollout تعریف کنید و پیشرفت/توقفش را کنترل کنید."],
      },
      {
        path: "/installations",
        title: "نصب‌ها",
        what: "کدام فیچر روی کدام فروشگاه نصب است و در چه وضعیتی (Pending/Installed/Failed).",
        steps: [
          "یک فیچر را روی یک فروشگاه نصب/ارتقا دهید (نسخه یا محدودهٔ نسخه).",
          "وضعیت نصب را دنبال کنید؛ اگر گیر کرد، خطا و دلیل این‌جاست.",
          "یادتان باشد: «entitlement» یعنی اجازهٔ داشتن فیچر (بخش مشتریان)، «نصب» یعنی واقعاً روی فروشگاه گذاشتنش.",
        ],
      },
      {
        path: "/plans",
        title: "پلن‌ها",
        what: "بسته‌های فروش؛ هر پلن مجموعه‌ای از فیچرهاست.",
        steps: ["پلن بسازید/ویرایش کنید و فیچرهای هر پلن را تعیین کنید؛ همین پلن به مشتری entitlement می‌دهد."],
      },
      {
        path: "/billing",
        title: "صورتحساب",
        what: "پرداخت‌ها و صورتحساب‌های مشتری‌ها.",
        steps: ["وضعیت پرداخت‌ها، فاکتورها و اشتراک‌ها را ببینید."],
      },
    ],
  },
  {
    group: "زیرساخت",
    entries: [
      {
        path: "/infrastructure",
        title: "زیرساخت",
        what: "وضعیت سرورها/سرویس‌های زیربنایی.",
        steps: ["سلامت اجزای زیرساخت را ببینید."],
      },
      {
        path: "/monitoring",
        title: "پایش",
        what: "نمودارها و متریک‌های زندهٔ سلامت.",
        steps: ["روند کارایی و در دسترس‌بودن را دنبال کنید."],
      },
      {
        path: "/alerts",
        title: "هشدارها",
        what: "هشدارهایی که وقتی چیزی از حد خارج می‌شود روشن می‌شوند.",
        steps: ["هشدارهای فعال را ببینید و رسیدگی کنید."],
      },
      {
        path: "/errors",
        title: "خطاها",
        what: "خطاهای ثبت‌شدهٔ سامانه.",
        steps: ["خطاها را بررسی و ریشه‌یابی کنید."],
      },
      {
        path: "/incidents",
        title: "رخدادها",
        what: "پرونده‌های اتفاق‌های مهم (قطعی/مشکل جدی).",
        steps: ["یک incident را باز/پیگیری/بسته کنید."],
      },
      {
        path: "/logs",
        title: "لاگ‌ها",
        what: "گزارش‌های خام سیستم.",
        steps: ["برای عیب‌یابی، لاگ‌ها را فیلتر و بخوانید."],
      },
    ],
  },
  {
    group: "حاکمیت",
    entries: [
      {
        path: "/reports",
        title: "گزارش‌ها",
        what: "گزارش‌های سطح سکو (نه سطح یک فروشگاه).",
        steps: ["آمار کلی سکو را ببینید."],
      },
      {
        path: "/access",
        title: "دسترسی",
        what: "کاربران اپراتور سکو و نقش‌هایشان.",
        steps: ["کاربر اپراتور اضافه/حذف کنید و نقش بدهید (SuperAdmin، Support، …)."],
        caution: "حذف کاربر یا گرفتن نقش، دسترسی او را فوری قطع می‌کند.",
      },
      {
        path: "/audit",
        title: "حسابرسی",
        what: "تاریخچهٔ این‌که چه کسی چه کاری کرد.",
        steps: ["برای پیگیری و پاسخ‌گویی، رخدادهای مهم را مرور کنید."],
      },
      {
        path: "/settings",
        title: "تنظیمات",
        what: "تنظیمات کلی سکو.",
        steps: ["پیکربندی‌های عمومی را با احتیاط تغییر دهید."],
      },
    ],
  },
];

/** Flat lookup by path, longest-prefix match (so /customers/:id shows customers help). */
export function helpForPath(pathname: string): HelpEntry | undefined {
  const all = HELP_SECTIONS.flatMap((s) => s.entries);
  const exact = all.find((e) => e.path === pathname);
  if (exact) return exact;
  const candidates = all
    .filter((e) => e.path !== "/" && pathname.startsWith(e.path))
    .sort((a, b) => b.path.length - a.path.length);
  return candidates[0];
}
