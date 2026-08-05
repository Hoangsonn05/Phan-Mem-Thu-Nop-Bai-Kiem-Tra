import assert from "node:assert/strict";
import test from "node:test";
import {
  normalizeExamArchiveObjectPath,
  normalizeSignedStorageUrl,
  readShortErrorCode,
  readSignedStorageValue,
} from "./signed-storage-url.mjs";

test("bucket-qualified exam metadata resolves to the object inside exam-archives", () => {
  assert.equal(
    normalizeExamArchiveObjectPath(
      "exam-archives/org/exam-files/file/exam.pdf",
    ),
    "org/exam-files/file/exam.pdf",
  );
  assert.equal(
    normalizeExamArchiveObjectPath("org/exam-files/file/exam.pdf"),
    "org/exam-files/file/exam.pdf",
  );
});

test("exam object path rejects another bucket, traversal, and backslashes", () => {
  for (
    const value of [
      "public-submission-archives/org/file.rar",
      "exam-archives/../file.pdf",
      "exam-archives/org\\file.pdf",
    ]
  ) {
    assert.throws(
      () => normalizeExamArchiveObjectPath(value),
      /PUBLIC_EXAM_OBJECT_PATH_INVALID/,
    );
  }
});

test("signed storage response accepts supported property names", () => {
  assert.equal(
    readSignedStorageValue({ signedURL: "/object/sign/a?token=one" }),
    "/object/sign/a?token=one",
  );
  assert.equal(
    readSignedStorageValue({ signedUrl: "/object/sign/a?token=two" }),
    "/object/sign/a?token=two",
  );
  assert.equal(readSignedStorageValue({}), null);
});

test("signed storage URL normalizes absolute, object, and storage paths", () => {
  const base = "https://project.supabase.co";
  assert.equal(
    normalizeSignedStorageUrl(
      base,
      "https://project.supabase.co/storage/v1/object/sign/a?token=one",
    ),
    "https://project.supabase.co/storage/v1/object/sign/a?token=one",
  );
  assert.equal(
    normalizeSignedStorageUrl(base, "/object/sign/a?token=two"),
    "https://project.supabase.co/storage/v1/object/sign/a?token=two",
  );
  assert.equal(
    normalizeSignedStorageUrl(base, "/storage/v1/object/sign/a?token=three"),
    "https://project.supabase.co/storage/v1/object/sign/a?token=three",
  );
});

test("signed storage URL rejects missing, external, and unsafe URLs", () => {
  const base = "https://project.supabase.co";
  for (
    const value of [
      "",
      "https://attacker.example/storage/v1/object/sign/a?token=x",
      "javascript:alert(1)",
      "file:///tmp/exam.pdf",
      "/unexpected/path",
    ]
  ) {
    assert.throws(
      () => normalizeSignedStorageUrl(base, value),
      /SIGNED_URL_INVALID/,
    );
  }
});

test("safe upstream code extraction prefers error then code then message", () => {
  assert.equal(
    readShortErrorCode({ error: "SIGNED_URL_FAILED", code: "42501" }),
    "SIGNED_URL_FAILED",
  );
  assert.equal(
    readShortErrorCode({
      code: "42501",
      message: "PUBLIC_EXAM_FILE_FORBIDDEN",
    }),
    "42501",
  );
  assert.equal(readShortErrorCode({ message: "not a stable code" }), null);
});
