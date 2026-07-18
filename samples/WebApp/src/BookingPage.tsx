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

import { useState } from "react";
import { Trans } from "react-i18next";
import { fetchQuote } from "./api/quotes";
import styles from "./BookingPage.module.css";

// A deliberately mixed page: copy that must be localized sits next to identifiers,
// routes, CSS classes and log lines that must be left exactly as they are.

const ENDPOINT = "/api/v1/bookings";
const ANALYTICS_EVENT = "booking_form_opened";

type Props = {
  vehicleId: string;
};

export default function BookingPage({ vehicleId }: Props) {
  const [days, setDays] = useState(3);

  async function submit() {
    console.log("submitting booking for", vehicleId, ENDPOINT);

    const quote = await fetchQuote(vehicleId, days);
    if (!quote) {
      alert("We could not price this rental. Please try again in a moment.");
      return;
    }

    if (!confirm("Charge your card now and confirm the booking?")) return;

    toast.success("Your booking is confirmed. Check your email for the details.");
  }

  return (
    <section className="booking-page" data-testid="booking-page">
      <h1>Book this car</h1>
      <p className={styles.lede}>
        Pick your dates and we will hold the vehicle for fifteen minutes.
      </p>

      <label htmlFor="days">Rental length in days</label>
      <input
        id="days"
        type="number"
        value={days}
        placeholder="How many days?"
        aria-label="Number of rental days"
        onChange={(e) => setDays(Number(e.target.value))}
      />

      <img src="/img/keys.png" alt="A set of car keys on a table" />

      <a href="https://example.com/terms" title="Read the rental terms">
        Rental terms
      </a>

      <button className="btn btn-primary" onClick={submit} title="Confirm and pay">
        Confirm booking
      </button>

      <p>
        <Trans i18nKey="AlreadyLocalized">This line is already localized.</Trans>
      </p>

      <span data-event={ANALYTICS_EVENT}>{vehicleId}</span>
    </section>
  );
}
