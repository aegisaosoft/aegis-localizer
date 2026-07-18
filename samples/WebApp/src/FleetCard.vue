<!--
  Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
  34 Middletown Ave, Atlantic Highlands, NJ 07716

  THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
  Aegis AO Soft LLC and Alexander Orlov.

  This code may be used, reproduced, modified, or distributed ONLY with the
  prior written permission of Aegis AO Soft LLC / Alexander Orlov.

  Author: Alexander Orlov
  Aegis AO Soft LLC
-->

<template>
  <!-- Static attributes below are copy; bound ones are expressions and must be skipped. -->
  <article class="fleet-card" :data-vehicle-id="vehicle.id">
    <h2>Available now</h2>
    <p>Free cancellation up to 24 hours before pickup.</p>

    <img :src="vehicle.photoUrl" alt="Photo of the vehicle" />

    <span>{{ vehicle.pricePerDay }}</span>
    <span>{{ $t("PerDay") }}</span>

    <input placeholder="Search the fleet" :title="dynamicTitle" />

    <button type="button" title="Reserve this vehicle" @click="reserve">
      Reserve
    </button>
  </article>
</template>

<script setup lang="ts">
import { defineProps } from "vue";
import type { Vehicle } from "../types/vehicle";

const props = defineProps<{ vehicle: Vehicle }>();

function reserve() {
  console.warn("reserve clicked", props.vehicle.id);
  alert("This vehicle is on hold for you for the next fifteen minutes.");
}
</script>

<style scoped>
.fleet-card {
  display: grid;
  font-family: "Inter", sans-serif;
}
</style>
