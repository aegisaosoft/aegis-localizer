/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import BookingPage from "./BookingPage";

// The bootstrap this tool generates. Imported for its side effect and imported first: it is what
// calls i18n.init(), and any component rendered before that would resolve keys against an
// uninitialised instance and show the key names instead of the copy.
import "../locales/i18n";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BookingPage vehicleId="demo" />
  </StrictMode>
);
