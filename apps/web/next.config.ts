import type { NextConfig } from "next";

const buildRevision = process.env.NEXT_PUBLIC_BUILD_REVISION
  ?? process.env.RAILWAY_GIT_COMMIT_SHA
  ?? process.env.GIT_COMMIT_SHA
  ?? "local";

const nextConfig: NextConfig = {
  env: {
    NEXT_PUBLIC_BUILD_REVISION: buildRevision,
  },
};

export default nextConfig;
