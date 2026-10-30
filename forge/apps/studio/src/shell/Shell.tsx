import { CadenceDots, Eyebrow, Panel } from "@4ge/kit";
import { Suspense } from "react";
import { NavLink, Outlet, isRouteErrorResponse, useLocation, useRouteError } from "react-router";
import { FEATURES } from "../features.js";
import { ModelStatus, ModelsProvider } from "./models.js";
import "./shell.css";
import { HealthProvider, ServerStatus } from "./ServerStatus.js";

/**
 * The studio shell: a left rail of features and one work surface. Holodex
 * opens this full screen; every feature mounts in the outlet. The IDE is a
 * full-screen layout, so on /ide the rail folds to icons (labels stay for
 * screen readers) and the build gets the width.
 */
export function Shell() {
  const { pathname } = useLocation();
  const compact = pathname.startsWith("/ide/");
  return (
    <HealthProvider>
      <ModelsProvider>
      <div className={`studio${compact ? " studio--compact" : ""}`}>
        <nav className="studio-rail" aria-label="4GE">
          <div className="studio-rail__brand">
            <span className="studio-rail__mark">4GE</span>
            <Eyebrow>Forge // Holodex</Eyebrow>
          </div>
          <ul className="studio-rail__list">
            {FEATURES.map((feature) => {
              const Icon = feature.icon;
              return (
                <li key={feature.id}>
                  <NavLink
                    to={feature.href}
                    className={({ isActive }) => `studio-rail__link${isActive ? " is-active" : ""}`}
                    data-testid={`rail-${feature.id}`}
                  >
                    <Icon aria-hidden="true" size={18} strokeWidth={1.75} />
                    <span className="studio-rail__label">{feature.label}</span>
                  </NavLink>
                </li>
              );
            })}
          </ul>
          <div className="studio-rail__foot">
            <ServerStatus />
            <ModelStatus />
          </div>
        </nav>
        <main className="studio-main">
          <Suspense fallback={<Loading />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
      </ModelsProvider>
    </HealthProvider>
  );
}

function Loading() {
  return (
    <div className="studio-loading" role="status" aria-live="polite">
      <CadenceDots tone="cream" label="Loading" />
    </div>
  );
}

/** Route error boundary: plain words, never a stack trace. */
export function RouteError() {
  const error = useRouteError();
  const message = isRouteErrorResponse(error)
    ? error.status === 404
      ? NOT_FOUND
      : `${error.status} ${error.statusText}`
    : error instanceof Error
      ? error.message
      : "Something went wrong while opening this page.";
  return <Problem message={message} />;
}

const NOT_FOUND = "There is no 4GE page at this address.";

/** The catch-all route for addresses no feature owns. */
export function NotFound() {
  return <Problem message={NOT_FOUND} />;
}

function Problem({ message }: { message: string }) {
  return (
    <section className="studio-pending" role="alert">
      <Panel className="studio-pending__panel">
        <Eyebrow>4GE // ERROR</Eyebrow>
        <h1 className="studio-pending__title">This page could not open</h1>
        <p className="studio-pending__body">{message}</p>
      </Panel>
    </section>
  );
}
