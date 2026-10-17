import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { App } from "./App";
import { AppStateProvider } from "./lib/app-state";
import "./index.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 5_000, retry: 1, refetchOnWindowFocus: false },
  },
});

// The server may run under a PathBase; index.html carries a relative <base>, so derive the router root from it.
const basename = new URL(".", document.baseURI).pathname.replace(/\/$/, "");

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipPrimitive.Provider delayDuration={300}>
        <BrowserRouter basename={basename}>
          <AppStateProvider>
            <App />
          </AppStateProvider>
        </BrowserRouter>
      </TooltipPrimitive.Provider>
    </QueryClientProvider>
  </StrictMode>,
);
