"use client";

import { useEffect } from "react";
import { BUILD_REVISION } from "@/lib/buildIdentity";

let buildLogged = false;

export function BuildIdentityLogger() {
  useEffect(() => {
    if (buildLogged) return;
    buildLogged = true;
    console.info(`Taslim Web build: ${BUILD_REVISION}`);
  }, []);

  return null;
}
