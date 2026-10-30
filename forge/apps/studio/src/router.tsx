import { Navigate, createBrowserRouter, type RouteObject } from "react-router";
import { FEATURES } from "./features.js";
import { NotFound, RouteError, Shell } from "./shell/Shell.js";

/** One route per feature folder, each lazy-loaded so the shell stays small. */
export const routes: RouteObject[] = [
  {
    path: "/",
    element: <Shell />,
    errorElement: <RouteError />,
    children: [
      { index: true, element: <Navigate to="/imagistation" replace /> },
      ...FEATURES.map(
        (feature): RouteObject => ({
          path: feature.path,
          errorElement: <RouteError />,
          lazy: async () => ({ Component: (await feature.load()).default }),
        }),
      ),
      { path: "*", element: <NotFound /> },
    ],
  },
];

export function createStudioRouter() {
  return createBrowserRouter(routes);
}
