// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { OpsView } from "./OpsView";

const snapshot = {
  jobs: { queued: 2, running: 1, failed24h: 3, oldestQueuedAgeSeconds: 900 },
  connectors: [{ connectorId: "local-folder", bindingCount: 4, lastSyncedAt: new Date().toISOString() }],
  rag: { collections: 5, chunks: 1234, lastIngestAt: new Date().toISOString() },
  notifications: { webhookConfigured: false },
  ai: { provider: "Mock", model: "gpt-4o-mini", monthTokens: 850, maxMonthlyTokens: 1000 },
};

const noWebhook = { webhookUrl: null, hasWebhookSecret: false };

function renderOps(data: unknown = snapshot, webhook: unknown = noWebhook) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    void init;
    return Promise.resolve({
      ok: true,
      json: () =>
        Promise.resolve(String(input).includes("/notification-settings") ? webhook : data),
    } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <OpsView />
    </QueryClientProvider>,
  );
  return fetchMock;
}

describe("OpsView (tenant health snapshot)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders the health cards from one snapshot call", async () => {
    renderOps();

    expect(await screen.findByText("Background jobs")).toBeTruthy();
    expect(screen.getByText("Failed (24h)")).toBeTruthy();
    // 15-minute-old queued job -> the backlog warning shows.
    expect(screen.getByText(/waited 15 minutes/)).toBeTruthy();
    expect(screen.getByText("local-folder")).toBeTruthy();
    // 850 of 1000 = 85% -> budget shown with percentage (and alarm styling).
    expect(screen.getByText("850 (85%)")).toBeTruthy();
  });

  it("reads cleanly with no budget and no connectors", async () => {
    renderOps({
      ...snapshot,
      jobs: { queued: 0, running: 0, failed24h: 0 },
      connectors: [],
      ai: { ...snapshot.ai, maxMonthlyTokens: 0 },
    });

    expect(await screen.findByText("No connectors enabled.")).toBeTruthy();
    expect(screen.getByText(/No monthly budget set/)).toBeTruthy();
  });

  it("notification delivery: prefills the URL and saving keeps the untouched secret (null)", async () => {
    const fetchMock = renderOps(snapshot, { webhookUrl: "https://example.com/hook", hasWebhookSecret: true });

    const url = (await screen.findByLabelText("Webhook URL")) as HTMLInputElement;
    await waitFor(() => expect(url.value).toBe("https://example.com/hook"));
    // A secret on file is never echoed — the field hints instead.
    expect(screen.getByPlaceholderText(/A secret is on file/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => {
      const put = fetchMock.mock.calls.find((c) => (c[1] as RequestInit | undefined)?.method === "PUT");
      expect(put).toBeTruthy();
      // Write-only contract: an untouched secret posts null (keep), never "".
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({
        webhookUrl: "https://example.com/hook",
        webhookSecret: null,
      });
    });
  });

  it("notification delivery: 'clear the stored secret' posts an empty string", async () => {
    const fetchMock = renderOps(snapshot, { webhookUrl: "https://example.com/hook", hasWebhookSecret: true });

    fireEvent.click(await screen.findByLabelText("Clear the stored secret"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find((c) => (c[1] as RequestInit | undefined)?.method === "PUT");
      expect(put).toBeTruthy();
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({
        webhookUrl: "https://example.com/hook",
        webhookSecret: "",
      });
    });
  });

  it("notification delivery: rejects a non-absolute URL before posting", async () => {
    renderOps();

    const url = (await screen.findByLabelText("Webhook URL")) as HTMLInputElement;
    fireEvent.change(url, { target: { value: "not-a-url" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/Enter an absolute http\(s\) URL/)).toBeTruthy();
  });
});
