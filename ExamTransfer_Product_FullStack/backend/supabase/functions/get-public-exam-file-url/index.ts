import {
  examArchiveBucket,
  normalizeExamArchiveObjectPath,
  normalizeSignedStorageUrl,
  readShortErrorCode,
  readSignedStorageValue,
} from "./signed-storage-url.mjs";

const corsHeaders = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers":
    "authorization, apikey, content-type, x-client-info",
  "access-control-allow-methods": "POST, OPTIONS",
};
const jsonHeaders = {
  ...corsHeaders,
  "content-type": "application/json; charset=utf-8",
};
const signedUrlLifetimeSeconds = 180;
const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export async function handler(request: Request): Promise<Response> {
  if (request.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: corsHeaders });
  }
  if (request.method !== "POST") {
    return errorResponse("METHOD_NOT_ALLOWED", 405, "request");
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const publishableKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  const authorization = request.headers.get("authorization");
  if (!supabaseUrl || !publishableKey || !serviceRoleKey) {
    return errorResponse("SERVER_NOT_CONFIGURED", 503, "configuration");
  }
  if (!authorization?.toLowerCase().startsWith("bearer ")) {
    return errorResponse("AUTHENTICATION_REQUIRED", 401, "authentication");
  }

  let body: { sessionId?: string; fileId?: string };
  try {
    body = await request.json();
  } catch {
    return errorResponse("INVALID_JSON", 400, "request");
  }
  if (
    !body.sessionId || !body.fileId || !uuidPattern.test(body.sessionId) ||
    !uuidPattern.test(body.fileId)
  ) {
    return errorResponse("INVALID_REQUEST", 400, "request");
  }

  // The student-token RPC remains the authorization boundary. The service key
  // is used only after the RPC returns one authorized immutable object path.
  const metadataResponse = await fetch(
    `${supabaseUrl}/rest/v1/rpc/get_public_exam_file_download`,
    {
      method: "POST",
      headers: { ...jsonHeaders, authorization, apikey: publishableKey },
      body: JSON.stringify({
        p_session_id: body.sessionId,
        p_file_id: body.fileId,
      }),
    },
  );
  const metadataPayload = await readJson(metadataResponse);
  if (!metadataResponse.ok) {
    const upstreamCode = readShortErrorCode(metadataPayload);
    if (
      upstreamCode === "PUBLIC_EXAM_FILE_FORBIDDEN" ||
      upstreamCode === "42501"
    ) {
      return errorResponse(
        "PUBLIC_EXAM_FILE_FORBIDDEN",
        403,
        "metadata",
        metadataResponse.status,
        upstreamCode,
      );
    }
    return errorResponse(
      "PUBLIC_EXAM_METADATA_FAILED",
      502,
      "metadata",
      metadataResponse.status,
      upstreamCode,
    );
  }

  if (!Array.isArray(metadataPayload)) {
    return errorResponse("PUBLIC_EXAM_METADATA_FAILED", 502, "metadata");
  }
  if (metadataPayload.length !== 1) {
    const missing = metadataPayload.length === 0;
    return errorResponse(
      missing ? "PUBLIC_EXAM_FILE_NOT_FOUND" : "PUBLIC_EXAM_METADATA_FAILED",
      missing ? 404 : 502,
      "metadata",
    );
  }
  const file = metadataPayload[0] as {
    object_path?: string;
    file_name?: string;
    size_bytes?: number;
    sha256?: string;
  };

  let objectPath: string;
  try {
    objectPath = normalizeExamArchiveObjectPath(file.object_path);
  } catch {
    return errorResponse("PUBLIC_EXAM_OBJECT_PATH_INVALID", 502, "metadata");
  }
  if (
    typeof file.file_name !== "string" || !file.file_name ||
    typeof file.size_bytes !== "number" || file.size_bytes < 0 ||
    typeof file.sha256 !== "string" || !/^[a-f0-9]{64}$/i.test(file.sha256)
  ) {
    return errorResponse("PUBLIC_EXAM_METADATA_FAILED", 502, "metadata");
  }

  const encodedPath = objectPath.split("/").map(encodeURIComponent).join("/");
  const signResponse = await fetch(
    `${supabaseUrl}/storage/v1/object/sign/${examArchiveBucket}/${encodedPath}`,
    {
      method: "POST",
      headers: {
        ...jsonHeaders,
        authorization: `Bearer ${serviceRoleKey}`,
        apikey: serviceRoleKey,
      },
      body: JSON.stringify({ expiresIn: signedUrlLifetimeSeconds }),
    },
  );
  const signPayload = await readJson(signResponse);
  if (!signResponse.ok) {
    return errorResponse(
      "STORAGE_SIGN_FAILED",
      502,
      "sign",
      signResponse.status,
      readShortErrorCode(signPayload),
    );
  }

  const signedValue = readSignedStorageValue(signPayload);
  if (!signedValue) {
    return errorResponse("SIGNED_URL_INVALID", 502, "sign");
  }

  let url: string;
  try {
    url = normalizeSignedStorageUrl(supabaseUrl, signedValue);
  } catch {
    return errorResponse("SIGNED_URL_INVALID", 502, "sign");
  }

  return new Response(
    JSON.stringify({
      url,
      expiresIn: signedUrlLifetimeSeconds,
      fileName: file.file_name,
      sizeBytes: file.size_bytes,
      sha256: file.sha256.toLowerCase(),
    }),
    { status: 200, headers: jsonHeaders },
  );
}

async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

function errorResponse(
  error: string,
  status: number,
  stage: string,
  upstreamStatus?: number,
  upstreamCode?: string | null,
): Response {
  return new Response(
    JSON.stringify({
      error,
      stage,
      ...(upstreamStatus ? { upstreamStatus } : {}),
      ...(upstreamCode ? { upstreamCode } : {}),
    }),
    { status, headers: jsonHeaders },
  );
}

if (import.meta.main) Deno.serve(handler);
