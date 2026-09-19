import { BUILD_REVISION } from "@/lib/buildIdentity";

export default function VersionPage() {
  return <main aria-label="Taslim Web version">Taslim Web build: {BUILD_REVISION}</main>;
}
