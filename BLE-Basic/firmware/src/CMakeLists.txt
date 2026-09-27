idf_component_register(SRCS main.c
                       REQUIRES esp_driver_i2c esp_driver_gpio esp_driver_usb_serial_jtag
                                esp_timer bt nvs_flash)
