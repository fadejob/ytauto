import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /* config options here */
  cacheComponents: true,
  partialPrefetching: true,
  // Baza sxemasi (bot/db/schema.sql): API birinchi so'rovda migratsiya qiladi - fayl serverless paketga kiritiladi.
  outputFileTracingIncludes: { "/api/**": ["./db/schema.sql"] },
};

export default nextConfig;
