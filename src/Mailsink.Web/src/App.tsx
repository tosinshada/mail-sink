import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell } from "./components/layout";
import { AccountsPage } from "./pages/accounts";
import { ChaosPage } from "./pages/chaos";
import { ComposePage } from "./pages/compose";
import { EmailDetailPage } from "./pages/email-detail";
import { EmailsPage } from "./pages/emails";
import { LogsPage } from "./pages/logs";
import { MailboxesPage } from "./pages/mailboxes";
import { MetricsPage } from "./pages/metrics";
import { SettingsPage } from "./pages/settings";
import { TemplatesPage } from "./pages/templates";

export function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<Navigate to="/emails" replace />} />
        <Route path="/emails" element={<EmailsPage />} />
        <Route path="/emails/:id" element={<EmailDetailPage />} />
        <Route path="/mailboxes" element={<MailboxesPage />} />
        <Route path="/mailboxes/:folder" element={<MailboxesPage />} />
        <Route path="/compose" element={<ComposePage />} />
        <Route path="/accounts" element={<AccountsPage />} />
        <Route path="/templates" element={<TemplatesPage />} />
        <Route path="/chaos" element={<ChaosPage />} />
        <Route path="/logs" element={<LogsPage />} />
        <Route path="/logs/:id" element={<LogsPage />} />
        <Route path="/metrics" element={<MetricsPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="*" element={<Navigate to="/emails" replace />} />
      </Route>
    </Routes>
  );
}
