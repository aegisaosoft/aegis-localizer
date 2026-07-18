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

import 'package:flutter/material.dart';

// Sample screen for the localizer. Deliberately mixes copy that must be translated with the
// strings that must not be touched: logs, asset paths, route names and widget keys.

void main() => runApp(const BookingApp());

class BookingApp extends StatelessWidget {
  const BookingApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Aegis Bookings',
      initialRoute: '/booking',
      routes: {'/booking': (context) => const BookingPage()},
      home: const BookingPage(),
    );
  }
}

class BookingPage extends StatefulWidget {
  const BookingPage({super.key});

  @override
  State<BookingPage> createState() => _BookingPageState();
}

class _BookingPageState extends State<BookingPage> {
  final _noteController = TextEditingController();

  static const _fallbackTitle = 'Booking unavailable';

  // Outside build: no BuildContext we can prove is in scope, so this copy is reported, not rewritten.
  Widget _buildFooter() {
    return Text('Questions? Call the depot on the number in your email.');
  }

  void _cancel() {
    // Diagnostics: developer-facing, never shown to a user.
    debugPrint('cancel tapped');
    print('cancelling booking');
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Your booking'),
        actions: [
          IconButton(
            icon: Image.asset('assets/images/cancel.png'),
            tooltip: 'Cancel this booking',
            onPressed: _cancel,
          ),
        ],
      ),
      body: Column(
        key: const ValueKey('booking_body'),
        children: [
          Text('Your card is charged when the trip starts.'),
          // Adjacent literals concatenate in Dart; both halves are one string.
          Text('Cancel before the pickup time '
              'and the deposit is refunded in full.'),
          TextField(
            controller: _noteController,
            decoration: InputDecoration(
              labelText: 'Note for the driver',
              hintText: 'Meet me at the north entrance',
              helperText: 'The driver sees this before pickup.',
            ),
          ),
          ListTile(
            title: Text('Trip summary'),
            subtitle: Text(_fallbackTitle),
          ),
          ElevatedButton(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text('Booking confirmed')),
              );
            },
            child: Text('Confirm booking'),
          ),
          _buildFooter(),
        ],
      ),
    );
  }
}
