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

#import "LegacyBookingViewController.h"

// Objective-C half of the sample: the same mix of copy, logs, keys and identifiers.
@implementation LegacyBookingViewController

- (void)viewDidLoad {
    [super viewDidLoad];

    self.title = @"Your booking";
    self.statusLabel.text = @"Waiting for approval";
    self.noteField.placeholder = @"Add a note for the driver";

    [self.payButton setTitle:@"Pay now" forState:UIControlStateNormal];

    // Not copy: an image asset, a defaults key and a reuse identifier.
    self.iconView.image = [UIImage imageNamed:@"booking-icon"];
    [[NSUserDefaults standardUserDefaults] setObject:@"seen" forKey:@"bookingIntroState"];
    [self.tableView registerClass:[UITableViewCell class] forCellReuseIdentifier:@"BookingCell"];

    // Diagnostics.
    NSLog(@"booking screen loaded");

    // Already localized: a second run must leave this alone.
    self.footerLabel.text = NSLocalizedString(@"BookingFooterNote", nil);
}

- (void)confirm {
    UIAlertController *alert = [UIAlertController
        alertControllerWithTitle:@"Confirm this booking?"
                         message:@"Your card will be charged when the trip starts."
                  preferredStyle:UIAlertControllerStyleAlert];

    [alert addAction:[UIAlertAction actionWithTitle:@"Confirm"
                                              style:UIAlertActionStyleDefault
                                            handler:nil]];

    [self presentViewController:alert animated:YES completion:nil];
}

@end
