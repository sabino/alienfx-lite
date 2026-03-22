#include "AlienFxLite.NativeBridge.h"

#include <algorithm>
#include <cwchar>
#include <sstream>
#include <string>
#include <vector>

#include "Upstream/AlienFXSDK/AlienFX_SDK.h"

using namespace AlienFX_SDK;

namespace
{
    Mappings g_mappings;
    bool g_enumerated = false;
    std::wstring g_lastError;

    template <typename TValue>
    TValue ClampValue(TValue value, TValue minimum, TValue maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        if (value > maximum)
        {
            return maximum;
        }

        return value;
    }

    void SetLastError(const std::wstring& message)
    {
        g_lastError = message;
    }

    void ClearLastError()
    {
        g_lastError.clear();
    }

    void EnsureEnumerated()
    {
        g_mappings.AlienFXEnumDevices();
        g_enumerated = true;
    }

    std::wstring BuildDeviceId(size_t index, const Afx_device& device)
    {
        wchar_t buffer[64]{};
        swprintf_s(buffer, L"%zu|%04X|%04X|%d", index, device.vid, device.pid, device.version);
        return buffer;
    }

    bool TryParseDeviceIndex(const wchar_t* deviceId, size_t& index)
    {
        if (deviceId == nullptr || deviceId[0] == L'\0')
        {
            return false;
        }

        const wchar_t* separator = wcschr(deviceId, L'|');
        if (separator == nullptr)
        {
            return false;
        }

        std::wstring indexText(deviceId, separator);
        try
        {
            index = static_cast<size_t>(std::stoul(indexText));
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    Afx_device* FindDeviceById(const wchar_t* deviceId)
    {
        if (!g_enumerated)
        {
            EnsureEnumerated();
        }

        size_t index = 0;
        if (!TryParseDeviceIndex(deviceId, index))
        {
            SetLastError(L"Invalid lighting device identifier.");
            return nullptr;
        }

        if (index >= g_mappings.fxdevs.size())
        {
            EnsureEnumerated();
            if (index >= g_mappings.fxdevs.size())
            {
                SetLastError(L"Lighting device is no longer available.");
                return nullptr;
            }
        }

        Afx_device& device = g_mappings.fxdevs[index];
        if (!device.present || device.dev == nullptr)
        {
            EnsureEnumerated();
            if (index >= g_mappings.fxdevs.size() || !g_mappings.fxdevs[index].present || g_mappings.fxdevs[index].dev == nullptr)
            {
                SetLastError(L"Lighting device is offline.");
                return nullptr;
            }
        }

        return &g_mappings.fxdevs[index];
    }

    Afx_colorcode ToColor(uint32_t color)
    {
        Afx_colorcode code{};
        code.r = static_cast<byte>((color >> 16) & 0xFF);
        code.g = static_cast<byte>((color >> 8) & 0xFF);
        code.b = static_cast<byte>(color & 0xFF);
        code.br = 0xFF;
        return code;
    }

    byte MapSpeedToTempo(uint8_t speedPercent)
    {
        int clamped = ClampValue(static_cast<int>(speedPercent), 0, 100);
        return static_cast<byte>(ClampValue(0xF0 - ((clamped * 0xE0) / 100), 0x10, 0xF0));
    }

    Afx_action MakeAction(byte type, uint32_t color, uint8_t speedPercent)
    {
        Afx_colorcode code = ToColor(color);
        return Afx_action{ type, 0x07, MapSpeedToTempo(speedPercent), code.r, code.g, code.b };
    }

    std::vector<Afx_action> BuildPhaseList(const AfxLiteLightAction& action)
    {
        switch (action.actionType)
        {
        case AlienFX_A_Color:
            return { MakeAction(AlienFX_A_Color, action.primaryColor, action.speedPercent) };
        case AlienFX_A_Pulse:
            return {
                MakeAction(AlienFX_A_Pulse, action.primaryColor, action.speedPercent),
                MakeAction(AlienFX_A_Pulse, 0x000000, action.speedPercent)
            };
        case AlienFX_A_Morph:
            return {
                MakeAction(AlienFX_A_Morph, action.primaryColor, action.speedPercent),
                MakeAction(AlienFX_A_Morph, action.secondaryColor, action.speedPercent)
            };
        case AlienFX_A_Breathing:
            return { MakeAction(AlienFX_A_Breathing, action.primaryColor, action.speedPercent) };
        case AlienFX_A_Spectrum:
            return { MakeAction(AlienFX_A_Spectrum, action.primaryColor, action.speedPercent) };
        case AlienFX_A_Rainbow:
            return { MakeAction(AlienFX_A_Rainbow, action.primaryColor, action.speedPercent) };
        default:
            return {};
        }
    }

    bool TryMapGlobalEffect(const Afx_device* device, const AfxLiteGlobalEffect& request, byte& effectType, byte& mode, byte& colorCount)
    {
        if (device == nullptr)
        {
            return false;
        }

        mode = device->version == API_V8 ? 1 : 0;

        switch (device->version)
        {
        case API_V5:
            switch (request.effectType)
            {
            case AlienFX_A_Color:
                effectType = 1;
                colorCount = 1;
                return true;
            case AlienFX_A_Pulse:
                effectType = 8;
                colorCount = 1;
                return true;
            case AlienFX_A_Morph:
                effectType = 9;
                colorCount = 2;
                return true;
            case AlienFX_A_Breathing:
                effectType = 2;
                colorCount = 1;
                return true;
            case AlienFX_A_Rainbow:
                effectType = 14;
                colorCount = 3;
                return true;
            default:
                return false;
            }
        case API_V8:
            switch (request.effectType)
            {
            case AlienFX_A_Color:
                effectType = 1;
                colorCount = 1;
                return true;
            case AlienFX_A_Pulse:
                effectType = 2;
                colorCount = 1;
                return true;
            case AlienFX_A_Morph:
                effectType = 1;
                colorCount = 2;
                return true;
            case AlienFX_A_Breathing:
                effectType = 7;
                colorCount = 1;
                return true;
            case AlienFX_A_Spectrum:
                effectType = 8;
                colorCount = 3;
                return true;
            case AlienFX_A_Rainbow:
                effectType = 16;
                colorCount = 3;
                return true;
            default:
                return false;
            }
        default:
            return false;
        }
    }

    bool ApplyBrightness(Afx_device* device, const uint8_t* lightIds, int32_t lightIdCount, int32_t brightnessPercent, int32_t includePowerLights)
    {
        if (brightnessPercent < 0 || device == nullptr || device->dev == nullptr)
        {
            return true;
        }

        if (device->version == API_V6)
        {
            return true;
        }

        std::vector<Afx_light> mappings;
        mappings.reserve(lightIdCount > 0 ? static_cast<size_t>(lightIdCount) : 0);
        for (int32_t index = 0; index < lightIdCount; index++)
        {
            mappings.push_back({ lightIds[index], { 0, 0 }, "" });
        }

        BYTE rawBrightness = static_cast<BYTE>(ClampValue((brightnessPercent * 255) / 100, 0, 255));
        bool needsUpdate = device->dev->SetBrightness(rawBrightness, 0xFF, &mappings, includePowerLights != 0);
        return !needsUpdate || device->dev->UpdateColors();
    }
}

extern "C" int32_t AfxLiteEnumerateDevices(
    AfxLiteDeviceInfo* devices,
    int32_t capacity,
    int32_t* totalCount)
{
    ClearLastError();
    if (capacity < 0)
    {
        SetLastError(L"Capacity must be zero or greater.");
        return AfxLiteBridgeStatus_InvalidArgument;
    }

    EnsureEnumerated();

    int32_t presentCount = 0;
    for (size_t index = 0; index < g_mappings.fxdevs.size(); index++)
    {
        const Afx_device& device = g_mappings.fxdevs[index];
        if (!device.present || device.dev == nullptr)
        {
            continue;
        }

        if (devices != nullptr && presentCount < capacity)
        {
            AfxLiteDeviceInfo& target = devices[presentCount];
            memset(&target, 0, sizeof(AfxLiteDeviceInfo));

            std::wstring deviceId = BuildDeviceId(index, device);
            wcsncpy_s(target.deviceId, deviceId.c_str(), _TRUNCATE);

            std::wstring description(device.dev->description.begin(), device.dev->description.end());
            wcsncpy_s(target.description, description.c_str(), _TRUNCATE);

            target.vendorId = device.vid;
            target.productId = device.pid;
            target.apiVersion = device.version;
            target.supportsGlobalEffects = device.version == API_V5 || device.version == API_V8;
            target.supportsBrightness = device.version != API_V6 && device.version != API_UNKNOWN;
            target.supportsPersistence = device.version == API_V2 || device.version == API_V3 || device.version == API_V4;
            target.present = 1;
        }

        presentCount++;
    }

    if (totalCount != nullptr)
    {
        *totalCount = presentCount;
    }

    return AfxLiteBridgeStatus_Ok;
}

extern "C" int32_t AfxLiteApplyLightActions(
    const wchar_t* deviceId,
    const AfxLiteLightAction* actions,
    int32_t actionCount,
    const uint8_t* brightnessLightIds,
    int32_t brightnessLightIdCount,
    int32_t brightnessPercent,
    int32_t persistDefault,
    int32_t includePowerLights)
{
    ClearLastError();
    if (actions == nullptr || actionCount <= 0)
    {
        SetLastError(L"At least one light action is required.");
        return AfxLiteBridgeStatus_InvalidArgument;
    }

    Afx_device* device = FindDeviceById(deviceId);
    if (device == nullptr || device->dev == nullptr)
    {
        return AfxLiteBridgeStatus_NotFound;
    }

    std::vector<Afx_lightblock> blocks;
    blocks.reserve(actionCount);
    for (int32_t index = 0; index < actionCount; index++)
    {
        std::vector<Afx_action> phases = BuildPhaseList(actions[index]);
        if (phases.empty())
        {
            SetLastError(L"Unsupported light action type.");
            return AfxLiteBridgeStatus_Unsupported;
        }

        blocks.push_back({ actions[index].lightId, phases });
    }

    if (!device->dev->SetMultiAction(&blocks, persistDefault != 0))
    {
        std::wstringstream stream;
        stream << L"Failed to apply lighting actions to device " << device->vid << L":" << device->pid << L".";
        SetLastError(stream.str());
        return AfxLiteBridgeStatus_Error;
    }

    if (!ApplyBrightness(device, brightnessLightIds, brightnessLightIdCount, brightnessPercent, includePowerLights))
    {
        SetLastError(L"Failed to update lighting brightness.");
        return AfxLiteBridgeStatus_Error;
    }

    return AfxLiteBridgeStatus_Ok;
}

extern "C" int32_t AfxLiteApplyGlobalEffect(
    const wchar_t* deviceId,
    AfxLiteGlobalEffect effect,
    int32_t brightnessPercent)
{
    ClearLastError();
    Afx_device* device = FindDeviceById(deviceId);
    if (device == nullptr || device->dev == nullptr)
    {
        return AfxLiteBridgeStatus_NotFound;
    }

    if (device->version != API_V5 && device->version != API_V8)
    {
        SetLastError(L"Global effects are not supported for this lighting device.");
        return AfxLiteBridgeStatus_Unsupported;
    }

    byte effectType = 0;
    byte mode = 0;
    byte colorCount = 0;
    if (!TryMapGlobalEffect(device, effect, effectType, mode, colorCount))
    {
        SetLastError(L"The requested global lighting effect is not supported for this device.");
        return AfxLiteBridgeStatus_Unsupported;
    }

    if (!device->dev->SetGlobalEffects(
            effectType,
            mode,
            colorCount,
            MapSpeedToTempo(effect.speedPercent),
            ToColor(effect.primaryColor),
            ToColor(effect.secondaryColor)))
    {
        SetLastError(L"Failed to apply the requested global effect.");
        return AfxLiteBridgeStatus_Error;
    }

    if (brightnessPercent >= 0)
    {
        std::vector<Afx_light> emptyMappings;
        BYTE rawBrightness = static_cast<BYTE>(ClampValue((brightnessPercent * 255) / 100, 0, 255));
        bool needsUpdate = device->dev->SetBrightness(rawBrightness, 0xFF, &emptyMappings, false);
        if (needsUpdate && !device->dev->UpdateColors())
        {
            SetLastError(L"Global effect applied, but brightness refresh failed.");
            return AfxLiteBridgeStatus_Error;
        }
    }

    return AfxLiteBridgeStatus_Ok;
}

extern "C" int32_t AfxLiteGetLastError(
    wchar_t* buffer,
    int32_t capacity)
{
    if (buffer == nullptr || capacity <= 0)
    {
        return static_cast<int32_t>(g_lastError.size());
    }

    wcsncpy_s(buffer, capacity, g_lastError.c_str(), _TRUNCATE);
    return static_cast<int32_t>(g_lastError.size());
}
