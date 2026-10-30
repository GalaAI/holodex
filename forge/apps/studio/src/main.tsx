import "@4ge/kit/tokens.css";
import "@4ge/kit/kit.css";
import "./studio.css";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import { createStudioRouter } from "./router.js";

const root = document.getElementById("root");
if (!root) throw new Error("The 4GE studio needs a #root element");

createRoot(root).render(
  <StrictMode>
    <RouterProvider router={createStudioRouter()} />
  </StrictMode>,
);
