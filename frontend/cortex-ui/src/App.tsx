import { BrowserRouter } from "react-router-dom";
import { AppShell } from "./routes/AppShell";

// The dev-server entry doubles as a product host's shell until @cortex/ui ships to npm: a host
// AppHost sets VITE_BRAND_NAME (e.g. "Casewell") and the top bar + tab title present the product,
// not the platform. Library consumers pass `branding` to CortexApp/AppShell directly instead.
const brandName = import.meta.env.VITE_BRAND_NAME as string | undefined;
if (brandName) {
  document.title = brandName;
}

export default function App() {
  return (
    // Opt into the React Router v7 behaviors now — silences the v6 future-flag console warnings and keeps
    // routing forward-compatible. Safe here: the app uses absolute links (no relative splat-path resolution).
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AppShell branding={brandName ? { name: brandName } : undefined} />
    </BrowserRouter>
  );
}
