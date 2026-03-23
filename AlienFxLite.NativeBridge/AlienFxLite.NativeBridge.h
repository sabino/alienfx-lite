#pragma once

#include <cstdint>

#ifdef ALIENFXLITEBRIDGE_EXPORTS
#define AFXL_API __declspec(dllexport)
#else
#define AFXL_API __declspec(dllimport)
#endif

extern "C" {

    enum AfxLiteBridgeStatus : int32_t {
        AfxLiteBridgeStatus_Ok = 0,
        AfxLiteBridgeStatus_Error = 1,
        AfxLiteBridgeStatus_NotFound = 2,
        AfxLiteBridgeStatus_InvalidArgument = 3,
        AfxLiteBridgeStatus_Unsupported = 4
    };

    struct AfxLiteDeviceInfo {
        wchar_t deviceId[64];
        wchar_t description[192];
        wchar_t devicePath[512];
        uint16_t vendorId;
        uint16_t productId;
        int32_t apiVersion;
        uint16_t reserved;
        uint8_t supportsGlobalEffects;
        uint8_t supportsBrightness;
        uint8_t supportsPersistence;
        uint8_t present;
    };

    struct AfxLiteLightAction {
        uint8_t lightId;
        uint8_t actionType;
        uint8_t speedPercent;
        uint8_t reserved;
        uint32_t primaryColor;
        uint32_t secondaryColor;
    };

    struct AfxLiteGlobalEffect {
        uint8_t effectType;
        uint8_t mode;
        uint8_t colorCount;
        uint8_t speedPercent;
        uint32_t primaryColor;
        uint32_t secondaryColor;
    };

    AFXL_API int32_t AfxLiteEnumerateDevices(
        AfxLiteDeviceInfo* devices,
        int32_t capacity,
        int32_t* totalCount);

    AFXL_API int32_t AfxLiteApplyLightActions(
        const wchar_t* deviceId,
        const AfxLiteLightAction* actions,
        int32_t actionCount,
        const uint8_t* brightnessLightIds,
        int32_t brightnessLightIdCount,
        int32_t brightnessPercent,
        int32_t persistDefault,
        int32_t includePowerLights);

    AFXL_API int32_t AfxLiteApplyGlobalEffect(
        const wchar_t* deviceId,
        AfxLiteGlobalEffect effect,
        int32_t brightnessPercent);

    AFXL_API int32_t AfxLiteGetLastError(
        wchar_t* buffer,
        int32_t capacity);
}
