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

import SwiftUI
import os

/// Sample screen for the localizer. Deliberately mixes copy that must be translated with the
/// strings that must not be touched: log lines, defaults keys, asset names and identifiers.
struct ContentView: View {
    @State private var email = ""
    @State private var showingCancelAlert = false

    private let logger = Logger(subsystem: "com.aegis.swiftapp", category: "booking")

    var body: some View {
        NavigationStack {
            Form {
                Section("Your details") {
                    TextField("Email address", text: $email)
                    SecureField("Password", text: .constant(""))
                    Toggle("Send me trip reminders", isOn: .constant(true))
                }

                Section(header: Text("Payment")) {
                    Text("Your card is charged when the trip starts.")
                    Button("Add a payment method") {
                        // Not copy: an analytics event name.
                        track(event: "payment_method_tapped")
                    }
                }

                Button("Cancel booking") {
                    showingCancelAlert = true
                }
                .accessibilityLabel("Cancel this booking")
                .help("Cancels the booking and refunds the deposit")
            }
            .navigationTitle("Booking")
            .searchable(text: .constant(""), prompt: "Search bookings")
            .alert("Cancel this booking?", isPresented: $showingCancelAlert) {
                Button("Keep it") { }
                Button("Cancel booking") { cancel() }
            }
        }
    }

    private func cancel() {
        // Diagnostics: developer-facing, never shown to a user.
        logger.debug("cancel tapped for booking")
        print("cancelling booking")
        NSLog("booking cancelled")

        // Machine strings: a defaults key, a notification name and an asset name.
        UserDefaults.standard.set(true, forKey: "hasCancelledOnce")
        NotificationCenter.default.post(name: Notification.Name("BookingCancelled"), object: nil)
        _ = Image("cancel-icon")
        _ = URL(string: "https://api.myeztoll.com/bookings")

        // Already localized: a second run must leave this alone.
        let confirmation = String(localized: "BookingCancelledConfirmation")
        print(confirmation)
    }

    private func track(event: String) {
        logger.info("event \(event)")
    }
}

/// UIKit half of the sample, for the property-assignment and setTitle paths.
final class LegacySummaryCell: UITableViewCell {
    static let reuseIdentifier = "LegacySummaryCell"

    private let titleLabel = UILabel()
    private let noteField = UITextField()
    private let payButton = UIButton(type: .system)

    func configure() {
        titleLabel.text = "Trip summary"
        noteField.placeholder = "Add a note for the driver"
        payButton.setTitle("Pay now", for: .normal)

        // Not copy: an accessibility identifier used by the UI tests.
        payButton.accessibilityIdentifier = "pay_now_button"
    }
}
