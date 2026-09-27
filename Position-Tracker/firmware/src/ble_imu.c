#include "ble_imu.h"

#include <math.h>
#include <stdio.h>
#include <string.h>
#include "driver/usb_serial_jtag.h"
#include "esp_log.h"
#include "nvs_flash.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

#define TAG "ble_imu"

#define SAMPLE_BYTES   16
#define MAX_BATCH      15          // 15 * 16 = 240 bytes, fits a 247-byte MTU

// 4f1e00XX-8a3c-4b7e-9d52-1c0de5a7a001, little-endian byte order.
#define IMU_UUID(xx) BLE_UUID128_INIT(0x01, 0xa0, 0xa7, 0xe5, 0x0d, 0x1c, 0x52, 0x9d, \
                                      0x7e, 0x4b, 0x3c, 0x8a, xx, 0x00, 0x1e, 0x4f)

static const ble_uuid128_t s_svc_uuid = IMU_UUID(0x01);
static const ble_uuid128_t s_data_uuid = IMU_UUID(0x02);
static const ble_uuid128_t s_info_uuid = IMU_UUID(0x03);
static const ble_uuid128_t s_status_uuid = IMU_UUID(0x04);

static const char *s_info = "";
static uint16_t s_data_handle;
static uint16_t s_status_handle;
static uint8_t s_status[7] = { 1, 0, 0, 0, 0, 0, 0xFF };
static uint8_t s_addr_type;
static volatile uint16_t s_conn = BLE_HS_CONN_HANDLE_NONE;
static volatile bool s_subscribed;

static uint8_t s_batch[MAX_BATCH * SAMPLE_BYTES];
static int s_batch_n;

static void advertise(void);

static int access_cb(uint16_t conn, uint16_t attr, struct ble_gatt_access_ctxt *ctxt, void *arg)
{
    if (ble_uuid_cmp(ctxt->chr->uuid, &s_info_uuid.u) == 0) {
        return os_mbuf_append(ctxt->om, s_info, strlen(s_info)) == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
    }
    if (ble_uuid_cmp(ctxt->chr->uuid, &s_status_uuid.u) == 0) {
        return os_mbuf_append(ctxt->om, s_status, sizeof(s_status)) == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
    }
    return BLE_ATT_ERR_READ_NOT_PERMITTED;   // data characteristic is notify-only
}

static const struct ble_gatt_svc_def s_services[] = {
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &s_svc_uuid.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            { .uuid = &s_data_uuid.u, .access_cb = access_cb,
              .val_handle = &s_data_handle, .flags = BLE_GATT_CHR_F_NOTIFY },
            { .uuid = &s_info_uuid.u, .access_cb = access_cb, .flags = BLE_GATT_CHR_F_READ },
            { .uuid = &s_status_uuid.u, .access_cb = access_cb, .val_handle = &s_status_handle,
              .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_NOTIFY },
            { 0 },
        },
    },
    { 0 },
};

// We keep advertising even while connected. If a PC app dies without
// disconnecting, Windows can hold that link open indefinitely; still being
// discoverable lets a new session find us (Windows then reuses the link). The
// most recent subscriber gets the stream, and older connections are dropped.
static int gap_event(struct ble_gap_event *ev, void *arg)
{
    switch (ev->type) {
    case BLE_GAP_EVENT_CONNECT:
        if (ev->connect.status == 0) {
            // Ask for a short connection interval (7.5-15 ms) for low latency.
            struct ble_gap_upd_params p = {
                .itvl_min = 6, .itvl_max = 12, .latency = 0,
                .supervision_timeout = 400, .min_ce_len = 0, .max_ce_len = 0,
            };
            ble_gap_update_params(ev->connect.conn_handle, &p);
        }
        advertise();
        break;
    case BLE_GAP_EVENT_DISCONNECT:
        if (ev->disconnect.conn.conn_handle == s_conn) {
            s_conn = BLE_HS_CONN_HANDLE_NONE;
            s_subscribed = false;
        }
        advertise();
        break;
    case BLE_GAP_EVENT_CONN_UPDATE: {
        struct ble_gap_conn_desc d;
        if (usb_serial_jtag_is_connected() &&
            ble_gap_conn_find(ev->conn_update.conn_handle, &d) == 0) {
            printf("#ble conn interval %.2f ms\n", d.conn_itvl * 1.25f);
        }
        break;
    }
    case BLE_GAP_EVENT_ADV_COMPLETE:
        advertise();
        break;
    case BLE_GAP_EVENT_SUBSCRIBE:
        if (ev->subscribe.attr_handle != s_data_handle) {
            break;
        }
        if (ev->subscribe.cur_notify) {
            uint16_t old = s_conn;
            s_conn = ev->subscribe.conn_handle;
            s_subscribed = true;
            if (old != BLE_HS_CONN_HANDLE_NONE && old != s_conn) {
                ble_gap_terminate(old, BLE_ERR_REM_USER_CONN_TERM);
            }
        } else if (ev->subscribe.conn_handle == s_conn) {
            s_subscribed = false;
        }
        break;
    default:
        break;
    }
    return 0;
}

static void advertise(void)
{
    struct ble_hs_adv_fields fields = { 0 };
    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    fields.name = (uint8_t *)BLE_IMU_DEVICE_NAME;
    fields.name_len = strlen(BLE_IMU_DEVICE_NAME);
    fields.name_is_complete = 1;
    ble_gap_adv_set_fields(&fields);

    struct ble_hs_adv_fields rsp = { 0 };
    rsp.uuids128 = (ble_uuid128_t *)&s_svc_uuid;
    rsp.num_uuids128 = 1;
    rsp.uuids128_is_complete = 1;
    ble_gap_adv_rsp_set_fields(&rsp);

    struct ble_gap_adv_params params = {
        .conn_mode = BLE_GAP_CONN_MODE_UND,
        .disc_mode = BLE_GAP_DISC_MODE_GEN,
    };
    int rc = ble_gap_adv_start(s_addr_type, NULL, BLE_HS_FOREVER, &params, gap_event, NULL);
    if (rc != 0 && rc != BLE_HS_EALREADY) {
        ESP_LOGE(TAG, "advertise failed: %d", rc);
    }
}

static void on_sync(void)
{
    ble_hs_id_infer_auto(0, &s_addr_type);
    advertise();
}

static void host_task(void *param)
{
    nimble_port_run();
    nimble_port_freertos_deinit();
}

void ble_imu_init(const char *info_text)
{
    s_info = info_text;

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);
    ESP_ERROR_CHECK(nimble_port_init());

    ble_hs_cfg.sync_cb = on_sync;
    ble_svc_gap_init();
    ble_svc_gatt_init();
    ESP_ERROR_CHECK(ble_gatts_count_cfg(s_services));
    ESP_ERROR_CHECK(ble_gatts_add_svcs(s_services));
    ble_svc_gap_device_name_set(BLE_IMU_DEVICE_NAME);

    nimble_port_freertos_init(host_task);
}

static void put_i16(uint8_t *p, float v)
{
    long x = lroundf(v);
    if (x > INT16_MAX) x = INT16_MAX;
    if (x < INT16_MIN) x = INT16_MIN;
    p[0] = (uint8_t)x;
    p[1] = (uint8_t)(x >> 8);
}

void ble_imu_push(int64_t t_us, const float accel_ms2[3], const float gyro_dps[3])
{
    uint16_t conn = s_conn;
    if (conn == BLE_HS_CONN_HANDLE_NONE || !s_subscribed) {
        s_batch_n = 0;
        return;
    }

    uint8_t *p = &s_batch[s_batch_n * SAMPLE_BYTES];
    uint32_t t = (uint32_t)t_us;
    memcpy(p, &t, 4);
    for (int i = 0; i < 3; i++) {
        put_i16(p + 4 + 2 * i, accel_ms2[i] * 500.0f);
        put_i16(p + 10 + 2 * i, gyro_dps[i] * 50.0f);
    }
    s_batch_n++;

    // Send right away for low latency. If the stack is out of buffers the
    // samples stay queued and go out together with the next one.
    int fit = (ble_att_mtu(conn) - 3) / SAMPLE_BYTES;
    if (fit < 1) fit = 1;
    if (fit > MAX_BATCH) fit = MAX_BATCH;
    int n = s_batch_n < fit ? s_batch_n : fit;
    struct os_mbuf *om = ble_hs_mbuf_from_flat(s_batch, n * SAMPLE_BYTES);
    if (om != NULL && ble_gatts_notify_custom(conn, s_data_handle, om) == 0) {
        memmove(s_batch, s_batch + n * SAMPLE_BYTES, (s_batch_n - n) * SAMPLE_BYTES);
        s_batch_n -= n;
    } else if (s_batch_n >= MAX_BATCH) {
        s_batch_n = 0;   // link is backed up; drop rather than stall sampling
    }
}

void ble_imu_set_status(uint8_t buttons, uint16_t recenter_count,
                        uint16_t battery_mv, uint8_t battery_pct)
{
    uint8_t v[7] = { 1, buttons, (uint8_t)recenter_count, (uint8_t)(recenter_count >> 8),
                     (uint8_t)battery_mv, (uint8_t)(battery_mv >> 8), battery_pct };
    if (memcmp(v, s_status, sizeof(v)) == 0) {
        return;
    }
    memcpy(s_status, v, sizeof(v));
    if (s_status_handle != 0) {
        ble_gatts_chr_updated(s_status_handle);   // notifies subscribed centrals
    }
}
