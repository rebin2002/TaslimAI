import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  getUserUploadSizeError,
  MAX_USER_UPLOAD_FILE_BYTES,
  USER_UPLOAD_SIZE_ERROR_MESSAGE,
} from "./fileUploadLimits";

beforeEach(() => {
  vi.resetModules();
  vi.unstubAllGlobals();
});

describe("normal file upload size limit", () => {
  it("accepts the exact configured client boundary", () => {
    expect(getUserUploadSizeError(MAX_USER_UPLOAD_FILE_BYTES)).toBeNull();
  });

  it("rejects a file one byte above the limit before sending it", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { api, ApiError } = await import("./api");
    const oversizedFile = { size: MAX_USER_UPLOAD_FILE_BYTES + 1 } as unknown as File;

    const failure = await api.uploadFile("workspace-1", oversizedFile).catch((error) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.code).toBe("FILE_TOO_LARGE");
    expect(failure.status).toBe(400);
    expect(failure.message).toBe(USER_UPLOAD_SIZE_ERROR_MESSAGE);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
