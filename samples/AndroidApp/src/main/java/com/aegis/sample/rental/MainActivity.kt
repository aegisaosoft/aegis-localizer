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

package com.aegis.sample.rental

import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import com.aegis.sample.rental.databinding.ActivityMainBinding
import com.google.android.material.snackbar.Snackbar

/**
 * Sample screen for the Android adapter: real UI copy mixed with the strings that must never be
 * localized - log messages, intent keys, tags and identifiers.
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // UI copy: these should all be extracted.
        setTitle("Booking details")
        binding.headline.text = "Your car is ready for pick-up"
        binding.confirm.setContentDescription("Confirms the booking and charges the deposit")

        Toast.makeText(this, "Deposit authorized", Toast.LENGTH_SHORT).show()
        Snackbar.make(binding.root, "Booking saved to your calendar", Snackbar.LENGTH_LONG).show()

        // Noise: diagnostics, keys and identifiers - none of these are user-visible.
        Log.d(TAG, "onCreate finished, booking id=$bookingId")
        Log.e("MainActivity", "Deposit authorization failed")
        println("about to start the details activity")

        val intent = Intent(ACTION_OPEN_BOOKING)
        intent.putExtra(EXTRA_BOOKING_ID, bookingId)
        intent.putExtra("com.aegis.sample.rental.EXTRA_SOURCE", "deep_link")

        if (bookingId == "unknown") {
            startActivity(intent)
        }
    }

    private fun confirmCancellation() {
        AlertDialog.Builder(this)
            .setTitle("Cancel this booking?")
            .setMessage("The deposit is returned to the original payment method.")
            .setPositiveButton("Cancel booking") { _, _ -> }
            .setNegativeButton("Keep it") { _, _ -> }
            .show()
    }

    companion object {
        private const val TAG = "MainActivity"
        private const val ACTION_OPEN_BOOKING = "com.aegis.sample.rental.OPEN_BOOKING"
        private const val EXTRA_BOOKING_ID = "com.aegis.sample.rental.BOOKING_ID"
        private const val bookingId = "unknown"
    }
}

@Composable
fun BookingSummary(total: String) {
    Text("Total due at pick-up")
    Text(text = "Includes taxes and airport fees")
    Text(text = total)
}
