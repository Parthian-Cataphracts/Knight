// Plain-language, Persian in-product help for the customer portal.
//
// The portal is the merchant's own panel — a different audience from the
// operations dashboard — so it has its own guide, keyed by route path. A "?"
// button shows the current page's entry and /portal/help lists them all. Written
// so a first-time shop owner can follow it without any prior knowledge.

export interface PortalHelpEntry {
  /** Route path this help belongs to. */
  path: string;
  /** Persian title shown in the drawer and the index. */
  title: string;
  /** One simple sentence: what this page is. */
  what: string;
  /** Step-by-step, what the merchant does here. */
  steps: string[];
  /** Optional caution for irreversible or billed actions. */
  caution?: string;
}

export const PORTAL_HELP_ENTRIES: PortalHelpEntry[] = [
  {
    path: "/portal",
    title: "خانهٔ پورتال",
    what: "پنلِ خودِ شما به‌عنوان صاحب کسب‌وکار: وضعیت اشتراک، فروشگاه‌تان، ادمین خودکار و راهنما — همه یک‌جا.",
    steps: [
      "اگر هنوز پلن نگرفته‌اید، «انتخاب پلن» را بزنید تا فروشگاه‌تان ساخته شود.",
      "بعد از خرید، کارت «اشتراک شما» پلن فعال و تاریخ تمدید را نشان می‌دهد.",
      "کارت «فروشگاه شما» وضعیت راه‌اندازی و دکمه‌های مدیریت/باز کردن فروشگاه را دارد.",
      "پایین صفحه، «سؤال‌های پرتکرار» جواب رایج‌ترین سؤال‌ها را می‌دهد.",
    ],
  },
  {
    path: "/portal/plans",
    title: "انتخاب پلن و فیچرها",
    what: "این‌جا پلن مناسب کسب‌وکارتان را انتخاب می‌کنید؛ هر پلن مجموعه‌ای از قابلیت‌هاست و بعضی پلن‌ها فیچر اختیاری هم دارند.",
    steps: [
      "بالای صفحه بین «ماهانه» و «سالانه» انتخاب کنید.",
      "روی کارت پلن دلخواه بزنید تا انتخاب شود (تیک و حاشیهٔ رنگی نشانهٔ انتخاب است).",
      "اگر پلن فیچر اختیاری داشته باشد، آن‌ها را روشن/خاموش کنید؛ «جمع تخمینی» پایین صفحه به‌روز می‌شود.",
      "«ادامه به پرداخت» را بزنید تا به درگاه بروید.",
    ],
    caution: "با پرداخت، اشتراک فعال و صورتحساب صادر می‌شود.",
  },
  {
    path: "/portal/pay",
    title: "پرداخت",
    what: "درگاه پرداخت اشتراک. در حالت آزمایشی هیچ مبلغی واقعاً گرفته نمی‌شود و فقط برای تکمیل مسیر است.",
    steps: [
      "«پرداخت موفق» را بزنید تا اشتراک و فیچرهای انتخابی فعال شوند.",
      "پس از پرداخت به‌صورت خودکار به خانهٔ پورتال برمی‌گردید.",
      "اگر منصرف شدید، «پرداخت ناموفق / لغو» را بزنید.",
    ],
  },
  {
    path: "/portal/stores",
    title: "مدیریت فروشگاه",
    what: "صفحهٔ مدیریت یک فروشگاه از داخل نایت: وضعیت، دسترسی به پنل مدیریت فروشگاه، و افزودن/حذف فیچرهای اختیاری.",
    steps: [
      "وضعیت فروشگاه بالای صفحه است؛ اگر «آماده» باشد دکمه‌های مدیریت و باز کردن فعال‌اند.",
      "«مدیریت فروشگاه» شما را به پنل ادمین فروشگاه می‌برد (محصولات، سفارش‌ها، افزونه‌ها).",
      "در «فیچرهای فروشگاه»، فیچرهای شاملِ پلن همیشه فعال‌اند؛ فیچرهای اختیاری را روشن/خاموش کنید و «اعمال و پرداخت» را بزنید.",
      "اگر فروشگاه هنوز آماده نشده، نوار پیشرفتِ راه‌اندازی را می‌بینید.",
    ],
  },
  {
    path: "/portal/auto-admin",
    title: "ادمین خودکار",
    what: "دستیارِ خودکارِ تولید و انتشار محتوا در کانال‌های فروشگاه‌تان (مثل تلگرام) بدون کار دستی.",
    steps: [
      "حالت خودکاری (autonomy) را انتخاب کنید: پیشنهاد بده / با تأیید منتشر کن / کاملاً خودکار.",
      "کانال‌ها و کلیدهای لازم را وارد کنید تا انتشار فعال شود.",
      "وضعیت و کارهای انجام‌شده را همین‌جا دنبال کنید.",
    ],
  },
];

/** Longest-prefix match, so /portal/stores/:id shows the store-management help. */
export function portalHelpForPath(pathname: string): PortalHelpEntry | undefined {
  const exact = PORTAL_HELP_ENTRIES.find((e) => e.path === pathname);
  if (exact) return exact;
  const candidates = PORTAL_HELP_ENTRIES.filter(
    (e) => e.path !== "/portal" && pathname.startsWith(e.path),
  ).sort((a, b) => b.path.length - a.path.length);
  return candidates[0] ?? PORTAL_HELP_ENTRIES.find((e) => e.path === "/portal");
}
