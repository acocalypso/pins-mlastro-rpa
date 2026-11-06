<template>
  <main class="mx-auto max-w-2xl p-4 space-y-5 text-gray-200">
    <header>
      <h1 class="text-2xl font-semibold">MLAstro RPA</h1>
      <p class="text-sm text-gray-400 mt-1">
        Automated polar alignment with your imaging camera and TPPA.
      </p>
    </header>
    <p
      v-if="error"
      role="alert"
      class="rounded-lg border border-red-700 bg-red-950/40 p-3"
    >
      {{ error }}
    </p>
    <p v-if="notice" role="status" class="text-cyan-300">{{ notice }}</p>
    <p
      v-if="motionFaulted"
      role="alert"
      class="rounded-lg border border-red-500 bg-red-950 p-3 font-semibold"
    >
      Motion is inhibited:
      {{ status.motionFault || "A movement command could not be confirmed." }}
      Disconnect and reconnect the RPA, then wait for it to be ready before
      moving again.
    </p>
    <p
      v-else-if="motionInhibited"
      role="status"
      class="rounded-lg border border-amber-600 bg-amber-950 p-3"
    >
      Motion is inhibited while the controller stops. Wait for the device status
      to update.
    </p>
    <section class="rounded-xl border border-gray-700 p-4 space-y-3">
      <h2 class="text-lg font-semibold">1. Connect the RPA</h2>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <label
          >Connection
          <select
            :value="connection.transport"
            :disabled="busy || !connectionReady || status.connected"
            class="field"
            @change="transport = $event.target.value"
          >
            <option v-if="!connectionReady" value="">
              Checking connection…
            </option>
            <option value="wifi">Wi-Fi</option>
            <option value="usb">USB serial</option>
          </select>
        </label>
        <label v-if="connection.transport === 'wifi'"
          >Hostname or IP address
          <input
            :value="connection.address"
            @input="address = $event.target.value"
            :placeholder="
              status.connected ? 'Connected address unavailable' : ''
            "
            :disabled="busy || status.connected"
            class="field"
            spellcheck="false"
          />
        </label>
        <label v-else-if="connection.transport === 'usb'"
          >USB device
          <select
            :value="connection.address"
            @change="usbSelection = $event.target.value"
            :disabled="busy || status.connected || usbLoading"
            class="field"
          >
            <option value="auto">Auto-detect</option>
            <option
              v-if="
                status.connected &&
                !usbDevices.some((device) => device.path === connection.address)
              "
              :value="connection.address"
            >
              Connected RPA - {{ connection.address }}
            </option>
            <option
              v-for="device in usbDevices"
              :key="device.path"
              :value="device.path"
            >
              {{ device.label }}{{ device.likelyRpa ? " (likely RPA)" : "" }} -
              {{ device.path }}
            </option>
          </select>
        </label>
      </div>
      <div class="flex flex-wrap gap-2">
        <button
          class="tns-btn-primary"
          :disabled="
            busy ||
            !connectionReady ||
            status.connected ||
            (transport === 'usb' && usbLoading)
          "
          @click="connect"
        >
          Connect
        </button>
        <button
          class="tns-btn-secondary"
          :disabled="!canDisconnect(status, jogActive, busy)"
          @click="disconnect"
        >
          Disconnect
        </button>
        <button
          class="tns-btn-secondary"
          :disabled="busy"
          @click="refreshConnection"
        >
          Refresh
        </button>
      </div>
      <p role="status">
        {{
          !connectionReady
            ? "Checking connection…"
            : status.connected
              ? "Connected"
              : "Disconnected"
        }}
        ·
        {{ status.deviceStatus || "Waiting for device" }}
      </p>
      <p
        v-if="status.connected && status.usbDevicePath"
        class="text-sm text-gray-400"
      >
        USB device: {{ status.usbDevicePath }}
      </p>
    </section>
    <section class="rounded-xl border border-gray-700 p-4 space-y-3">
      <h2 class="text-lg font-semibold">Manual adjustments</h2>
      <p class="text-sm text-gray-400">
        Choose a bounded step or hold an arrow to jog. Release to stop.
        Direction reversals may take longer while firmware backlash compensation
        runs.
      </p>
      <label
        >Movement mode
        <select
          v-model="manualMode"
          class="field max-w-48"
          :disabled="manualBlocked || jogActive"
        >
          <option value="step">Step</option>
          <option value="jog">Hold to jog</option>
        </select>
      </label>
      <label v-if="manualMode === 'step'"
        >Step size (arcseconds)
        <select
          v-model.number="manualStep"
          class="field max-w-48"
          :disabled="manualBlocked"
        >
          <option :value="5">5″</option>
          <option :value="10">10″</option>
          <option :value="30">30″</option>
          <option :value="60">60″</option>
          <option :value="120">120″</option>
        </select>
      </label>
      <div
        class="arrow-pad"
        role="group"
        aria-label="Manual altitude and azimuth adjustments"
      >
        <button
          class="pad-key pad-up"
          aria-label="Altitude up"
          :disabled="manualBlocked && !jogActive"
          @pointerdown="pressArrow($event, 'alt', 1)"
          @pointerup="releaseArrow"
          @pointercancel="releaseArrow"
          @lostpointercapture="releaseArrow"
          @click="arrowClick('alt', 1)"
        >
          <span aria-hidden="true">↑</span><small>Alt up</small>
        </button>
        <button
          class="pad-key pad-left"
          aria-label="Azimuth left"
          :disabled="manualBlocked && !jogActive"
          @pointerdown="pressArrow($event, 'az', -1)"
          @pointerup="releaseArrow"
          @pointercancel="releaseArrow"
          @lostpointercapture="releaseArrow"
          @click="arrowClick('az', -1)"
        >
          <span aria-hidden="true">←</span><small>Az left</small>
        </button>
        <button
          class="pad-key pad-stop"
          aria-label="Stop movement"
          @click="stop"
        >
          STOP
        </button>
        <button
          class="pad-key pad-right"
          aria-label="Azimuth right"
          :disabled="manualBlocked && !jogActive"
          @pointerdown="pressArrow($event, 'az', 1)"
          @pointerup="releaseArrow"
          @pointercancel="releaseArrow"
          @lostpointercapture="releaseArrow"
          @click="arrowClick('az', 1)"
        >
          <span aria-hidden="true">→</span><small>Az right</small>
        </button>
        <button
          class="pad-key pad-down"
          aria-label="Altitude down"
          :disabled="manualBlocked && !jogActive"
          @pointerdown="pressArrow($event, 'alt', -1)"
          @pointerup="releaseArrow"
          @pointercancel="releaseArrow"
          @lostpointercapture="releaseArrow"
          @click="arrowClick('alt', -1)"
        >
          <span aria-hidden="true">↓</span><small>Alt down</small>
        </button>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <label
          >Speed level<select
            v-model.number="deviceSettings.speedLevel"
            class="field"
            :disabled="manualBlocked || jogActive"
          >
            <option v-for="level in 5" :key="level" :value="level">
              {{ level }}
            </option>
          </select></label
        >
        <label
          >AZ backlash steps<input
            v-model.number="deviceSettings.azSteps"
            type="number"
            min="0"
            max="10000000"
            step="1"
            class="field"
            :disabled="manualBlocked || jogActive"
        /></label>
        <label
          >ALT backlash steps<input
            v-model.number="deviceSettings.altSteps"
            type="number"
            min="0"
            step="1"
            class="field"
            :disabled="manualBlocked || jogActive"
        /></label>
      </div>
      <label
        ><input
          v-model="deviceSettings.backlashEnabled"
          type="checkbox"
          :disabled="manualBlocked || jogActive"
        />
        Enable backlash compensation</label
      >
      <div class="flex gap-2">
        <button
          class="tns-btn-secondary"
          :disabled="manualBlocked || jogActive"
          @click="saveDeviceSettings"
        >
          Save device settings</button
        ><button
          class="tns-btn-secondary"
          :disabled="deviceSettingsBlocked"
          @click="loadDeviceSettings"
        >
          Reload device settings
        </button>
      </div>
      <p v-if="!deviceSettingsLoaded" class="text-sm text-gray-400">
        Load device settings before using manual controls.
      </p>
      <p v-if="status.jogError" role="alert" class="text-sm text-red-300">
        {{ status.jogError }}
      </p>
      <p v-if="status.backlash" class="text-sm text-gray-400">
        Firmware backlash:
        {{ status.backlash.enabled ? "enabled" : "disabled" }} · AZ
        {{ status.backlash.azSteps }} steps · ALT
        {{ status.backlash.altSteps }} steps
      </p>
      <p role="status">{{ status.manualMoveStatus || "Idle" }}</p>
      <p
        v-if="status.automationEnabled || running"
        class="text-sm text-gray-400"
      >
        Manual controls are locked during alignment. Stop alignment first.
      </p>
    </section>
    <section class="rounded-xl border border-gray-700 p-4 space-y-3">
      <h2 class="text-lg font-semibold">2. Choose the finish tolerance</h2>
      <p class="text-sm text-gray-400">
        Use TPPA's existing camera, exposure and starting-position settings. A
        connected, unparked mount is required for the automatic three-point
        sweep.
      </p>
      <label
        >Alignment tolerance (arcminutes)
        <input
          v-model.number="tolerance"
          type="number"
          min="0.1"
          max="10"
          step="0.1"
          class="field max-w-48"
          :disabled="
            busy ||
            status.automationEnabled ||
            running ||
            jogActive ||
            status.jogActive
          "
        />
      </label>
      <button
        class="tns-btn-secondary"
        :disabled="
          busy ||
          status.automationEnabled ||
          running ||
          jogActive ||
          status.jogActive
        "
        @click="saveSettings"
      >
        Save settings
      </button>
      <details>
        <summary class="cursor-pointer text-gray-400">
          Advanced correction settings
        </summary>
        <div class="grid grid-cols-1 sm:grid-cols-3 gap-3 mt-3">
          <label
            >Correction fraction<input
              v-model.number="factor"
              type="number"
              min="0.1"
              max="1"
              step="0.1"
              class="field"
              :disabled="
                busy ||
                status.automationEnabled ||
                jogActive ||
                status.jogActive
              "
          /></label>
          <label
            >Maximum step (arcmin)<input
              v-model.number="maxStep"
              type="number"
              min="0.1"
              max="10"
              step="0.1"
              class="field"
              :disabled="
                busy ||
                status.automationEnabled ||
                jogActive ||
                status.jogActive
              "
          /></label>
          <label
            >Settling (seconds)<input
              v-model.number="settle"
              type="number"
              min="1"
              max="60"
              step="1"
              class="field"
              :disabled="
                busy ||
                status.automationEnabled ||
                jogActive ||
                status.jogActive
              "
          /></label>
        </div>
        <label class="block mt-3"
          ><input
            v-model="pauseFirst"
            type="checkbox"
            :disabled="
              busy || status.automationEnabled || jogActive || status.jogActive
            "
          />
          Pause before the first correction</label
        >
        <p class="text-sm text-gray-400 mt-2">
          Firmware backlash compensation and hardware limits are preserved.
        </p>
      </details>
    </section>
    <section class="rounded-xl border border-gray-700 p-4 space-y-3">
      <h2 class="text-lg font-semibold">3. Align</h2>
      <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
      <p v-if="notice" role="status" class="text-cyan-300">{{ notice }}</p>
      <p class="text-sm text-gray-400">
        Start selects MLAstro for TPPA, enables the controller and begins the
        three-point sweep. Ensure the telescope has room to slew.
      </p>
      <div class="flex flex-wrap gap-2">
        <button
          class="tns-btn-primary"
          :disabled="
            busy ||
            stopping ||
            pendingStart ||
            motionInhibited ||
            jogActive ||
            status.jogActive ||
            running ||
            !status.connected ||
            status.deviceStatus !== 'READY'
          "
          @click="start"
        >
          Start automated alignment
        </button>
        <button
          class="tns-btn-secondary"
          :disabled="busy || !running"
          @click="pauseResume"
        >
          {{ paused ? "Resume" : "Pause" }}
        </button>
        <button class="tns-btn-danger" @click="stop">Stop alignment</button>
      </div>
      <p role="status">{{ status.sessionStatus || "Idle" }}</p>
      <p v-if="lastProgress" class="text-sm break-words">{{ lastProgress }}</p>
      <p v-if="status.automationEnabled" class="text-sm text-cyan-300">
        Correction controller enabled
      </p>
      <p class="text-sm text-gray-400">
        The backend runs alignment even if you leave this page. Reopen it to
        check status.
      </p>
    </section>
  </main>
</template>

<script setup>
import { onMounted, onBeforeUnmount, ref, watch, computed } from "vue";
import { apiStore } from "@/store/store";
import { useTppaStore } from "@/store/tppaStore";
import apiService from "@/services/apiService";
import websocket from "@/services/websocketTppa";
import { buildTppaStartMessage } from "@/utils/tppaStart";
import { rpaRequest } from "../api";
import { startAutomaticAlignment } from "../workflow";
import { createJogController } from "../jog";
import { connectionForDisplay } from "../connection";
import { canDisconnect, motionRecovery } from "../motion";

const rig = apiStore();
const tppa = useTppaStore();
const status = ref({});
const motionFaulted = computed(() => motionRecovery(status.value).faulted);
const motionInhibited = computed(() => motionRecovery(status.value).inhibited);
const connectionReady = computed(
  () => typeof status.value.connected === "boolean",
);
const manualStep = ref(30);
const manualMode = ref("step");
const jogActive = ref(false);
const deviceSettingsLoaded = ref(false);
let settingsGeneration = 0;
const deviceSettings = ref({
  speedLevel: 1,
  backlashEnabled: false,
  azSteps: 0,
  altSteps: 0,
});
let activePointer = null;
let suppressClick = false;
const jog = createJogController({
  request: rpaRequest,
  onError: (e) => {
    error.value = e.message;
  },
  onChange: (active) => {
    jogActive.value = active;
  },
});
const transport = ref("wifi");
const address = ref("mlastrorpa.local");
const usbSelection = ref("auto");
const connection = computed(() =>
  connectionForDisplay(status.value, {
    transport: transport.value,
    address: address.value,
    usbSelection: usbSelection.value,
  }),
);
const usbDevices = ref([]);
const usbLoading = ref(false);
let usbDiscoveryGeneration = 0;
const tolerance = ref(1);
const factor = ref(0.5);
const maxStep = ref(0.5);
const settle = ref(2);
const pauseFirst = ref(false);
const busy = ref(false);
const pendingStart = ref(false);
const stopping = ref(false);
const running = ref(false);
const paused = ref(tppa.isPause);
const error = ref("");
const notice = ref("");
const lastProgress = ref("");
const manualBlocked = computed(
  () =>
    busy.value ||
    stopping.value ||
    running.value ||
    pendingStart.value ||
    motionInhibited.value ||
    !status.value.connected ||
    status.value.automationEnabled ||
    status.value.manualMoveActive ||
    status.value.jogActive ||
    jogActive.value ||
    !deviceSettingsLoaded.value ||
    status.value.deviceStatus !== "READY",
);
const deviceSettingsBlocked = computed(
  () =>
    busy.value ||
    stopping.value ||
    running.value ||
    pendingStart.value ||
    motionInhibited.value ||
    !status.value.connected ||
    status.value.automationEnabled ||
    status.value.manualMoveActive ||
    status.value.jogActive ||
    jogActive.value ||
    status.value.deviceStatus !== "READY",
);
let poll,
  unsubscribe,
  startGeneration = 0;
let startAckTimer;
watch(
  () => status.value.connected,
  (connected, previous) => {
    if (connected && !previous) loadDeviceSettings();
    if (!connected) {
      ++settingsGeneration;
      deviceSettingsLoaded.value = false;
      releaseJog();
    }
  },
);
watch(motionInhibited, (inhibited) => {
  if (inhibited) releaseJog();
});
watch(transport, (value) => {
  if (value === "usb") discoverUsb();
  else ++usbDiscoveryGeneration;
});
async function discoverUsb() {
  const generation = ++usbDiscoveryGeneration;
  usbLoading.value = true;
  try {
    const result = await rpaRequest("usb-devices");
    if (generation !== usbDiscoveryGeneration) return;
    usbDevices.value = Array.isArray(result.devices) ? result.devices : [];
    if (
      usbSelection.value !== "auto" &&
      !usbDevices.value.some((device) => device.path === usbSelection.value)
    )
      usbSelection.value = "auto";
  } catch (e) {
    if (generation === usbDiscoveryGeneration) error.value = e.message;
  } finally {
    if (generation === usbDiscoveryGeneration) usbLoading.value = false;
  }
}
const refreshConnection = () =>
  run(async () => {
    if (transport.value === "usb") await discoverUsb();
  });
async function refresh() {
  status.value = await rpaRequest("status");
  const info = await apiService.getTppaInfo();
  if (!info?.Success) throw new Error("TPPA status is unavailable.");
  running.value = Boolean(info.IsRunning);
  if (running.value) {
    pendingStart.value = false;
    clearTimeout(startAckTimer);
  }
}
async function run(operation) {
  if (busy.value || stopping.value) return;
  busy.value = true;
  error.value = "";
  notice.value = "";
  try {
    await operation();
    await refresh();
  } catch (e) {
    error.value = e.message;
  } finally {
    busy.value = false;
  }
}
const connect = () =>
  run(async () => {
    const result = await rpaRequest("connect", {
      transport: transport.value,
      address:
        transport.value === "usb" ? usbSelection.value : address.value.trim(),
    });
    if (!result.connected) throw new Error("RPA connection was not accepted.");
    notice.value = "Connected. Save a positive tolerance before starting.";
  });
const disconnect = () => run(() => rpaRequest("disconnect", {}));
function releaseJog() {
  activePointer = null;
  jog.stop();
}
function pressArrow(event, axis, direction) {
  if (manualMode.value === "step") suppressClick = false;
  if (
    manualMode.value !== "jog" ||
    manualBlocked.value ||
    jogActive.value ||
    event.button !== 0
  )
    return;
  event.preventDefault();
  suppressClick = true;
  activePointer = event.pointerId;
  event.currentTarget.setPointerCapture(event.pointerId);
  jog.start(axis, direction);
}
function releaseArrow(event) {
  if (activePointer === event.pointerId) releaseJog();
}
function arrowClick(axis, direction) {
  if (suppressClick) {
    suppressClick = false;
    return;
  }
  if (manualMode.value === "step") manualMove(axis, direction);
}
async function loadDeviceSettings() {
  const generation = ++settingsGeneration;
  deviceSettingsLoaded.value = false;
  try {
    const settings = await rpaRequest("device-settings");
    if (generation !== settingsGeneration || !status.value.connected) return;
    if (
      !Number.isInteger(settings.speedLevel) ||
      settings.speedLevel < 1 ||
      settings.speedLevel > 5 ||
      typeof settings.backlashEnabled !== "boolean" ||
      !Number.isInteger(settings.azSteps) ||
      settings.azSteps < 0 ||
      settings.azSteps > 10000000 ||
      !Number.isInteger(settings.altSteps) ||
      settings.altSteps < 0 ||
      settings.altSteps > 10000000
    )
      throw new Error(
        "Device settings are invalid. Reload them before using manual controls.",
      );
    deviceSettings.value = settings;
    deviceSettingsLoaded.value = true;
  } catch (e) {
    if (generation === settingsGeneration) error.value = e.message;
  }
}
function saveDeviceSettings() {
  return run(async () => {
    const settings = deviceSettings.value;
    if (
      !Number.isInteger(settings.speedLevel) ||
      settings.speedLevel < 1 ||
      settings.speedLevel > 5 ||
      !Number.isInteger(settings.azSteps) ||
      settings.azSteps < 0 ||
      settings.azSteps > 10000000 ||
      !Number.isInteger(settings.altSteps) ||
      settings.altSteps < 0 ||
      settings.altSteps > 10000000
    )
      throw new Error(
        "Choose a speed from 1 to 5 and whole backlash steps from 0 to 10000000.",
      );
    await rpaRequest("device-settings", { ...settings });
    notice.value = "Device settings saved.";
  });
}
const manualMove = (axis, direction) => {
  if (manualBlocked.value) return;
  return run(async () => {
    await rpaRequest("move", { axis, direction, arcseconds: manualStep.value });
    notice.value =
      "Manual step requested. Wait for movement completion before the next step.";
  });
};
const saveSettings = () =>
  run(async () => {
    if (
      !Number.isFinite(tolerance.value) ||
      tolerance.value < 0.1 ||
      tolerance.value > 10
    )
      throw new Error("Choose a tolerance from 0.1 to 10 arcminutes.");
    await rpaRequest("settings", {
      factor: factor.value,
      maxStepArcMin: maxStep.value,
      settleSeconds: settle.value,
      pauseBeforeFirstCorrection: pauseFirst.value,
    });
    const result = await apiService.postTppaOptions({
      AlignmentTolerance: tolerance.value,
    });
    if (!result?.Success || (result.Failures && Object.keys(result.Failures).length))
      throw new Error("TPPA did not save the tolerance.");
    const saved = await apiService.getTppaOptions();
    if (!saved?.Success || Number(saved.Options?.AlignmentTolerance?.Value) !== tolerance.value)
      throw new Error("TPPA did not retain the requested tolerance. Settings were not confirmed saved.");
    notice.value = "Correction settings and TPPA tolerance saved.";
  });
const start = () =>
  run(async () => {
    const generation = ++startGeneration;
    const current = () => {
      if (generation !== startGeneration)
        throw new Error("Start cancelled by Stop.");
    };
    await startAutomaticAlignment({
      isCancelled: () => generation !== startGeneration,
      verify: async () => {
        await refresh();
        current();
        if (running.value) throw new Error("TPPA is already running.");
        if (!rig.cameraInfo?.Connected || !rig.mountInfo?.Connected)
          throw new Error("Connect the imaging camera and mount first.");
        if (rig.mountInfo.AtPark)
          throw new Error("Unpark the mount before starting alignment.");
        const options = await apiService.getTppaOptions();
        current();
        if (!(options?.Options?.AlignmentTolerance?.Value > 0))
          throw new Error("Save a positive alignment tolerance first.");
        if (!websocket.isOpen()) await websocket.connect();
        current();
        if (!websocket.isOpen())
          throw new Error("TPPA connection is unavailable.");
        tppa.settingsReady = false;
        await tppa.loadTppaSettings();
        if (!tppa.settingsReady)
          throw new Error(
            "Could not load current TPPA camera settings. Open TPPA settings and retry.",
          );
        current();
        return buildTppaStartMessage(
          { ...tppa.settings, ManualMode: false },
          rig,
        );
      },
      select: () => rpaRequest("select-controller", {}),
      enable: () => rpaRequest("automation", { enabled: true }),
      send: (message) => {
        pendingStart.value = true;
        const sent = websocket.sendMessage(JSON.stringify(message));
        if (!sent) pendingStart.value = false;
        return sent;
      },
      stop: () => rpaRequest("stop", {}),
    });
    notice.value = "Start sent. Watch TPPA progress for confirmation.";
    clearTimeout(startAckTimer);
    startAckTimer = setTimeout(async () => {
      if (!pendingStart.value) return;
      try {
        const info = await apiService.getTppaInfo();
        if (info?.Success && info.IsRunning) {
          pendingStart.value = false;
          running.value = true;
          return;
        }
        await stop();
        error.value =
          "TPPA did not confirm the start. Stop was requested; inspect status before retrying.";
      } catch (e) {
        error.value = `Start could not be confirmed: ${e.message}. Use Stop before retrying.`;
      }
    }, 10000);
  });
const pauseResume = () =>
  run(async () => {
    if (!websocket.isOpen()) throw new Error("TPPA connection is unavailable.");
    const sent = websocket.sendMessage(
      JSON.stringify({
        Action: paused.value ? "resume-alignment" : "pause-alignment",
      }),
    );
    if (!sent) throw new Error("TPPA pause/resume could not be sent.");
  });
async function stop() {
  releaseJog();
  if (stopping.value) return;
  stopping.value = true;
  clearTimeout(startAckTimer);
  ++startGeneration;
  pendingStart.value = false;
  error.value = "";
  notice.value = "";
  try {
    const results = await Promise.allSettled([
      rpaRequest("stop", {}),
      (async () => {
        if (!websocket.isOpen()) await websocket.connect();
        if (!websocket.isOpen())
          throw new Error("TPPA stop could not be sent.");
        if (
          !websocket.sendMessage(JSON.stringify({ Action: "stop-alignment" }))
        )
          throw new Error("TPPA stop could not be sent.");
      })(),
    ]);
    const failure = results.find((result) => result.status === "rejected");
    if (failure) throw failure.reason;
    await refresh();
    notice.value =
      "Stop requested; check the device status confirms it is idle.";
  } catch (e) {
    error.value = e.message;
  } finally {
    stopping.value = false;
  }
}
onMounted(() => {
  window.addEventListener("blur", releaseJog);
  document.addEventListener("visibilitychange", visibilityChanged);
  unsubscribe = websocket.addMessageListener((message) => {
    const response = message?.Response;
    if (response === "stopped procedure") {
      pendingStart.value = false;
      running.value = false;
      paused.value = false;
    } else if (response === "paused procedure") {
      paused.value = true;
    } else if (response === "resumed procedure") {
      paused.value = false;
    } else if (response) {
      running.value = true;
      paused.value = response.Status === "Paused";
      lastProgress.value =
        typeof response === "string"
          ? response
          : response.Status || "Alignment running";
    }
  });
  run(async () => {
    status.value = await rpaRequest("status");
    const settings = await rpaRequest("settings");
    factor.value = settings.factor;
    maxStep.value = Number.isFinite(settings.maxStepArcMin) &&
      settings.maxStepArcMin >= 0.1 && settings.maxStepArcMin <= 10
      ? settings.maxStepArcMin : 0.5;
    settle.value = settings.settleSeconds;
    pauseFirst.value = settings.pauseBeforeFirstCorrection;
    const options = await apiService.getTppaOptions();
    tolerance.value = options?.Options?.AlignmentTolerance?.Value ?? 0;
  });
  poll = setInterval(() => {
    if (!busy.value)
      refresh().catch((e) => {
        error.value = e.message;
      });
  }, 3000);
});
function visibilityChanged() {
  if (document.hidden) releaseJog();
}
onBeforeUnmount(() => {
  ++settingsGeneration;
  ++usbDiscoveryGeneration;
  releaseJog();
  window.removeEventListener("blur", releaseJog);
  document.removeEventListener("visibilitychange", visibilityChanged);
  clearInterval(poll);
  unsubscribe?.();
  ++startGeneration;
});
</script>

<style scoped>
.arrow-pad {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  grid-template-areas: ". up ." "left stop right" ". down .";
  gap: 0.5rem;
  max-width: 340px;
  margin: 1rem auto;
}
.pad-key {
  touch-action: none;
  min-height: 74px;
  border: 1px solid #475569;
  border-radius: 0.75rem;
  background: #1e293b;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
}
.pad-key span {
  font-size: 2rem;
  line-height: 1.1;
}
.pad-key small {
  font-size: 0.75rem;
}
.pad-key:focus-visible {
  outline: 3px solid #22d3ee;
}
.pad-up {
  grid-area: up;
}
.pad-left {
  grid-area: left;
}
.pad-right {
  grid-area: right;
}
.pad-down {
  grid-area: down;
}
.pad-stop {
  grid-area: stop;
  background: #9f1239;
  font-weight: 700;
}
.field {
  display: block;
  width: 100%;
  margin-top: 0.35rem;
  padding: 0.6rem;
  border: 1px solid #475569;
  border-radius: 0.5rem;
  background: #1e293b;
  color: #e2e8f0;
}
button:disabled {
  opacity: 0.45;
  cursor: default;
}
label {
  font-size: 0.9rem;
}
</style>
