export interface Env {
  ROOTFS_BUCKET: R2Bucket;
  ROOTFS_PREFIX?: string;
}

const IMMUTABLE_CACHE_CONTROL = "public, max-age=31536000, immutable";
const IMAGE_CACHE_CONTROL = "public, max-age=60, s-maxage=300";

export const onRequestGet: PagesFunction<Env> = async (context) => {
  return handleRequest(context);
};

export const onRequestHead: PagesFunction<Env> = async (context) => {
  return handleRequest(context, true);
};

async function handleRequest(context: EventContext<Env, string, unknown>, headOnly = false): Promise<Response> {
  const pathParam = context.params.path;
  const pathSegments = Array.isArray(pathParam)
    ? pathParam
    : typeof pathParam === "string" && pathParam.length > 0
      ? [pathParam]
      : [];
  const objectPrefix = (context.env.ROOTFS_PREFIX ?? "rootfs").replace(/^\/+|\/+$/g, "");
  const objectKey = [objectPrefix, ...pathSegments].join("/");

  const objectHead = await context.env.ROOTFS_BUCKET.head(objectKey);
  if (!objectHead) {
    return new Response("Not Found", {
      status: 404,
      headers: buildBaseHeaders(cacheControlForKey(objectKey)),
    });
  }

  const rangeRequest = parseRangeHeader(context.request.headers.get("Range"), objectHead.size);
  if (rangeRequest === "invalid") {
    return new Response("Invalid Range", {
      status: 416,
      headers: {
        ...buildObjectHeaders(objectKey, objectHead),
        "Content-Range": `bytes */${objectHead.size}`,
      },
    });
  }

  const getOptions = rangeRequest && rangeRequest.type !== "full"
    ? { range: toR2Range(rangeRequest) }
    : undefined;
  const objectBody = await context.env.ROOTFS_BUCKET.get(objectKey, getOptions);
  if (!objectBody) {
    return new Response("Not Found", {
      status: 404,
      headers: buildBaseHeaders(cacheControlForKey(objectKey)),
    });
  }

  const headers = buildObjectHeaders(objectKey, objectBody);
  if (rangeRequest && rangeRequest.type !== "full") {
    headers["Content-Range"] = `bytes ${rangeRequest.start}-${rangeRequest.end}/${objectHead.size}`;
    headers["Content-Length"] = String(rangeRequest.end - rangeRequest.start + 1);
  } else {
    headers["Content-Length"] = String(objectHead.size);
  }

  return new Response(headOnly ? null : objectBody.body, {
    status: rangeRequest && rangeRequest.type !== "full" ? 206 : 200,
    headers,
  });
}

function buildObjectHeaders(objectKey: string, object: R2Object | R2ObjectBody): Record<string, string> {
  const cacheControl = cacheControlForKey(objectKey);
  const headers = buildBaseHeaders(cacheControl);
  const contentType = object.httpMetadata?.contentType ?? inferContentType(objectKey);
  if (contentType) {
    headers["Content-Type"] = contentType;
  }
  if (object.httpEtag) {
    headers["ETag"] = object.httpEtag;
  }
  return headers;
}

function buildBaseHeaders(cacheControl: string): Record<string, string> {
  return {
    "Accept-Ranges": "bytes",
    "Cache-Control": cacheControl,
    "Cross-Origin-Resource-Policy": "same-origin",
    "Vary": "Range",
  };
}

function cacheControlForKey(objectKey: string): string {
  return objectKey.endsWith("/image.json") ? IMAGE_CACHE_CONTROL : IMMUTABLE_CACHE_CONTROL;
}

type FullRange = { type: "full" };
type BoundedRange = { type: "bounded"; start: number; end: number };
type SuffixRange = { type: "suffix"; suffix: number; start: number; end: number };
type ParsedRange = FullRange | BoundedRange | SuffixRange;

function parseRangeHeader(headerValue: string | null, size: number): ParsedRange | "invalid" | null {
  if (!headerValue) {
    return null;
  }

  const match = /^bytes=(\d*)-(\d*)$/.exec(headerValue.trim());
  if (!match) {
    return "invalid";
  }

  const [, startText, endText] = match;
  if (!startText && !endText) {
    return "invalid";
  }

  if (!startText) {
    const suffix = Number.parseInt(endText, 10);
    if (!Number.isFinite(suffix) || suffix <= 0) {
      return "invalid";
    }
    if (suffix >= size) {
      return { type: "full" };
    }
    return { type: "suffix", suffix, start: size - suffix, end: size - 1 };
  }

  const start = Number.parseInt(startText, 10);
  if (!Number.isFinite(start) || start < 0 || start >= size) {
    return "invalid";
  }

  if (!endText) {
    return { type: "bounded", start, end: size - 1 };
  }

  const end = Number.parseInt(endText, 10);
  if (!Number.isFinite(end) || end < start) {
    return "invalid";
  }

  return {
    type: "bounded",
    start,
    end: Math.min(end, size - 1),
  };
}

function toR2Range(range: BoundedRange | SuffixRange): R2Range {
  if (range.type === "suffix") {
    return { suffix: range.suffix };
  }
  return { offset: range.start, length: range.end - range.start + 1 };
}

function inferContentType(objectKey: string): string | null {
  if (objectKey.endsWith(".json")) {
    return "application/json; charset=utf-8";
  }
  if (objectKey.endsWith(".wasm")) {
    return "application/wasm";
  }
  if (objectKey.endsWith(".mjs") || objectKey.endsWith(".js")) {
    return "text/javascript; charset=utf-8";
  }
  if (objectKey.endsWith(".svg")) {
    return "image/svg+xml";
  }
  if (objectKey.endsWith(".txt")) {
    return "text/plain; charset=utf-8";
  }
  return null;
}
