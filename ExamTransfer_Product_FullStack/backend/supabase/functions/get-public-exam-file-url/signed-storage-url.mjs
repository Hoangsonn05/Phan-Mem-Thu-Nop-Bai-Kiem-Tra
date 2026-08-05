export const examArchiveBucket = "exam-archives";

const shortCodePattern = /^[A-Z0-9][A-Z0-9_.-]{0,79}$/;

export function normalizeExamArchiveObjectPath(value) {
  if (typeof value !== "string") {
    throw new Error("PUBLIC_EXAM_OBJECT_PATH_INVALID");
  }
  const candidate = value.trim();
  if (!candidate || candidate.length > 1024 || candidate.includes("\\")) {
    throw new Error("PUBLIC_EXAM_OBJECT_PATH_INVALID");
  }

  const prefix = `${examArchiveBucket}/`;
  let objectPath = candidate.startsWith(prefix)
    ? candidate.slice(prefix.length)
    : candidate;
  if (/^[a-z0-9][a-z0-9-]*-archives\//i.test(objectPath)) {
    throw new Error("PUBLIC_EXAM_OBJECT_PATH_INVALID");
  }

  const segments = objectPath.split("/");
  if (
    segments.some((segment) => !segment || segment === "." || segment === "..")
  ) {
    throw new Error("PUBLIC_EXAM_OBJECT_PATH_INVALID");
  }
  return segments.join("/");
}

export function readSignedStorageValue(payload) {
  if (!payload || typeof payload !== "object") return null;
  for (const name of ["signedURL", "signedUrl"]) {
    const value = payload[name];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return null;
}

export function normalizeSignedStorageUrl(supabaseUrl, signedValue) {
  const projectUrl = new URL(supabaseUrl);
  if (!isHttp(projectUrl)) throw new Error("SIGNED_URL_INVALID");
  if (typeof signedValue !== "string" || !signedValue.trim()) {
    throw new Error("SIGNED_URL_INVALID");
  }

  const value = signedValue.trim();
  let resolved;
  if (/^https?:\/\//i.test(value)) {
    resolved = new URL(value);
  } else if (value.startsWith("/storage/v1/")) {
    resolved = new URL(value, projectUrl);
  } else if (value.startsWith("/object/")) {
    resolved = new URL(`/storage/v1${value}`, projectUrl);
  } else {
    throw new Error("SIGNED_URL_INVALID");
  }

  if (!isHttp(resolved) || resolved.origin !== projectUrl.origin) {
    throw new Error("SIGNED_URL_INVALID");
  }
  return resolved.toString();
}

export function readShortErrorCode(payload) {
  if (!payload || typeof payload !== "object") return null;
  for (const name of ["error", "code", "message"]) {
    const value = payload[name];
    if (typeof value === "string" && shortCodePattern.test(value)) return value;
  }
  return null;
}

function isHttp(value) {
  return value.protocol === "http:" || value.protocol === "https:";
}
