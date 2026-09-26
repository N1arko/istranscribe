// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/AudioHardwareTapping.h>
#import <CoreAudio/CATapDescription.h>
#import <AVFAudio/AVFAudio.h>
#import <AudioToolbox/AudioToolbox.h>
#import <AudioUnit/AudioUnit.h>
#import <ApplicationServices/ApplicationServices.h>
#import <ServiceManagement/ServiceManagement.h>
#import <Security/Security.h>
#import <UserNotifications/UserNotifications.h>
#import <libproc.h>
#import <mach/mach_time.h>

typedef struct {
    uint32_t object_id;
    int32_t pid;
    uint8_t running_output;
    uint8_t running_input;
    char name[256];
    char bundle_id[256];
} ist_process;

typedef struct {
    uint32_t object_id;
    uint8_t has_output;
    uint8_t has_input;
    uint8_t is_default_output;
    uint8_t is_default_input;
    char uid[256];
    char name[256];
} ist_device;

typedef struct {
    int32_t pid;
    uint32_t window_id;
    uint8_t is_foreground;
    char owner_name[256];
    char title[512];
    char accessible_controls[2048];
} ist_window;

typedef void (*ist_pcm_callback)(void *context, const float *samples, uint32_t frames,
                                 uint32_t channels, double sample_rate, uint64_t host_time);

typedef NS_ENUM(int32_t, ist_permission) {
    ist_permission_undetermined = 0,
    ist_permission_denied = 1,
    ist_permission_granted = 2
};

static NSString *const ist_accessibility_requested_key = @"permission-requested-accessibility";
static NSString *const ist_screen_capture_requested_key = @"permission-requested-screen-capture";
static NSString *const ist_system_audio_requested_key = @"permission-requested-system-audio";

static int32_t permission_state_with_request_history(BOOL granted, NSString *requested_key) {
    NSUserDefaults *defaults = NSUserDefaults.standardUserDefaults;
    if (granted) {
        [defaults setBool:YES forKey:requested_key];
        return ist_permission_granted;
    }
    return [defaults boolForKey:requested_key] ? ist_permission_denied : ist_permission_undetermined;
}

static void mark_permission_requested(NSString *requested_key) {
    [NSUserDefaults.standardUserDefaults setBool:YES forKey:requested_key];
}

@interface ISTCapture : NSObject
@property AudioObjectID tapID;
@property AudioObjectID aggregateID;
@property AudioDeviceIOProcID ioProcID;
@property(strong) AVAudioEngine *engine;
@property ist_pcm_callback callback;
@property void *context;
@end

@implementation ISTCapture
@end

static AudioObjectPropertyAddress address(AudioObjectPropertySelector selector,
                                          AudioObjectPropertyScope scope) {
    return (AudioObjectPropertyAddress){ selector, scope, kAudioObjectPropertyElementMain };
}

static bool read_value(AudioObjectID object, AudioObjectPropertySelector selector,
                       AudioObjectPropertyScope scope, void *value, UInt32 size) {
    AudioObjectPropertyAddress property = address(selector, scope);
    return AudioObjectGetPropertyData(object, &property, 0, NULL, &size, value) == noErr;
}

static NSString *read_string(AudioObjectID object, AudioObjectPropertySelector selector) {
    CFStringRef value = NULL;
    if (!read_value(object, selector, kAudioObjectPropertyScopeGlobal, &value, sizeof(value)) || value == NULL) {
        return @"";
    }
    return CFBridgingRelease(value);
}

static void copy_utf8(NSString *value, char *destination, size_t capacity) {
    if (capacity == 0) return;
    destination[0] = '\0';
    [value getCString:destination maxLength:capacity encoding:NSUTF8StringEncoding];
    destination[capacity - 1] = '\0';
}

static NSArray<NSNumber *> *object_list(AudioObjectPropertySelector selector) {
    AudioObjectPropertyAddress property = address(selector, kAudioObjectPropertyScopeGlobal);
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &property, 0, NULL, &size) != noErr || size == 0) {
        return @[];
    }
    NSUInteger count = size / sizeof(AudioObjectID);
    AudioObjectID *objects = calloc(count, sizeof(AudioObjectID));
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &property, 0, NULL, &size, objects) != noErr) {
        free(objects);
        return @[];
    }
    NSMutableArray<NSNumber *> *result = [NSMutableArray arrayWithCapacity:count];
    for (NSUInteger index = 0; index < count; index++) [result addObject:@(objects[index])];
    free(objects);
    return result;
}

static pid_t parent_process_id(pid_t pid) {
    struct proc_bsdinfo info = {0};
    int bytes = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info));
    return bytes == sizeof(info) ? (pid_t)info.pbi_ppid : 0;
}

static NSString *ancestor_application_bundle_id(pid_t pid) {
    pid_t current = parent_process_id(pid);
    for (NSUInteger depth = 0; current > 1 && depth < 12; depth++) {
        NSRunningApplication *application =
            [NSRunningApplication runningApplicationWithProcessIdentifier:current];
        if (application.bundleIdentifier.length > 0) return application.bundleIdentifier;
        current = parent_process_id(current);
    }
    return @"";
}

__attribute__((visibility("default")))
int32_t ist_audio_permission(void) {
    if (@available(macOS 14.0, *)) {
        switch (AVAudioApplication.sharedInstance.recordPermission) {
            case AVAudioApplicationRecordPermissionGranted: return ist_permission_granted;
            case AVAudioApplicationRecordPermissionDenied: return ist_permission_denied;
            default: return ist_permission_undetermined;
        }
    }
    return ist_permission_undetermined;
}

__attribute__((visibility("default")))
double ist_host_ticks_per_second(void) {
    mach_timebase_info_data_t info = {0};
    if (mach_timebase_info(&info) != KERN_SUCCESS || info.numer == 0) return 1000000000.0;
    return 1000000000.0 * (double)info.denom / (double)info.numer;
}

__attribute__((visibility("default")))
int32_t ist_enumerate_processes(ist_process *destination, int32_t capacity) {
    NSArray<NSNumber *> *objects = object_list(kAudioHardwarePropertyProcessObjectList);
    int32_t written = 0;
    for (NSNumber *number in objects) {
        if (written >= capacity || destination == NULL) break;
        AudioObjectID object = number.unsignedIntValue;
        pid_t pid = 0;
        UInt32 output = 0, input = 0;
        if (!read_value(object, kAudioProcessPropertyPID, kAudioObjectPropertyScopeGlobal, &pid, sizeof(pid))) continue;
        read_value(object, kAudioProcessPropertyIsRunningOutput, kAudioObjectPropertyScopeGlobal, &output, sizeof(output));
        read_value(object, kAudioProcessPropertyIsRunningInput, kAudioObjectPropertyScopeGlobal, &input, sizeof(input));
        NSString *bundle = read_string(object, kAudioProcessPropertyBundleID);
        NSString *ancestorBundle = ancestor_application_bundle_id(pid);
        if (ancestorBundle.length > 0) bundle = ancestorBundle;
        char processName[PROC_PIDPATHINFO_MAXSIZE] = {0};
        proc_name(pid, processName, sizeof(processName));
        destination[written].object_id = object;
        destination[written].pid = pid;
        destination[written].running_output = output != 0;
        destination[written].running_input = input != 0;
        copy_utf8(processName[0] == '\0' ? bundle : [NSString stringWithUTF8String:processName], destination[written].name, 256);
        copy_utf8(bundle, destination[written].bundle_id, 256);
        written++;
    }
    return destination == NULL ? (int32_t)objects.count : written;
}

static bool device_has_stream(AudioObjectID device, AudioObjectPropertyScope scope) {
    AudioObjectPropertyAddress property = address(kAudioDevicePropertyStreams, scope);
    UInt32 size = 0;
    return AudioObjectGetPropertyDataSize(device, &property, 0, NULL, &size) == noErr && size > 0;
}

__attribute__((visibility("default")))
int32_t ist_enumerate_devices(ist_device *destination, int32_t capacity) {
    NSArray<NSNumber *> *objects = object_list(kAudioHardwarePropertyDevices);
    AudioObjectID defaultOutput = kAudioObjectUnknown, defaultInput = kAudioObjectUnknown;
    read_value(kAudioObjectSystemObject, kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, &defaultOutput, sizeof(defaultOutput));
    read_value(kAudioObjectSystemObject, kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal, &defaultInput, sizeof(defaultInput));
    int32_t written = 0;
    for (NSNumber *number in objects) {
        if (written >= capacity || destination == NULL) break;
        AudioObjectID object = number.unsignedIntValue;
        bool output = device_has_stream(object, kAudioObjectPropertyScopeOutput);
        bool input = device_has_stream(object, kAudioObjectPropertyScopeInput);
        if (!output && !input) continue;
        destination[written].object_id = object;
        destination[written].has_output = output;
        destination[written].has_input = input;
        destination[written].is_default_output = object == defaultOutput;
        destination[written].is_default_input = object == defaultInput;
        copy_utf8(read_string(object, kAudioDevicePropertyDeviceUID), destination[written].uid, 256);
        copy_utf8(read_string(object, kAudioObjectPropertyName), destination[written].name, 256);
        written++;
    }
    return destination == NULL ? (int32_t)objects.count : written;
}

static void append_accessible_control_names(AXUIElementRef element, NSMutableArray<NSString *> *names,
                                            NSUInteger *visited, NSUInteger depth) {
    if (!element || *visited >= 64 || depth > 6) return;
    (*visited)++;
    CFTypeRef roleValue = NULL;
    if (AXUIElementCopyAttributeValue(element, kAXRoleAttribute, &roleValue) == kAXErrorSuccess && roleValue) {
        if (CFGetTypeID(roleValue) == CFStringGetTypeID()
            && [(__bridge NSString *)roleValue isEqualToString:(__bridge NSString *)kAXButtonRole]) {
            CFTypeRef titleValue = NULL;
            if (AXUIElementCopyAttributeValue(element, kAXTitleAttribute, &titleValue) == kAXErrorSuccess && titleValue) {
                if (CFGetTypeID(titleValue) == CFStringGetTypeID()) {
                    NSString *title = (__bridge NSString *)titleValue;
                    if (title.length > 0 && names.count < 64) [names addObject:title];
                }
                CFRelease(titleValue);
            }
            CFTypeRef descriptionValue = NULL;
            if (AXUIElementCopyAttributeValue(element, kAXDescriptionAttribute, &descriptionValue) == kAXErrorSuccess && descriptionValue) {
                if (CFGetTypeID(descriptionValue) == CFStringGetTypeID()) {
                    NSString *description = (__bridge NSString *)descriptionValue;
                    if (description.length > 0 && names.count < 64) [names addObject:description];
                }
                CFRelease(descriptionValue);
            }
        }
        CFRelease(roleValue);
    }

    CFTypeRef childrenValue = NULL;
    if (AXUIElementCopyAttributeValue(element, kAXChildrenAttribute, &childrenValue) != kAXErrorSuccess || !childrenValue) return;
    if (CFGetTypeID(childrenValue) == CFArrayGetTypeID()) {
        NSArray *children = (__bridge NSArray *)childrenValue;
        for (id child in children) {
            if (CFGetTypeID((__bridge CFTypeRef)child) != AXUIElementGetTypeID()) continue;
            append_accessible_control_names((__bridge AXUIElementRef)child, names, visited, depth + 1);
            if (*visited >= 64) break;
        }
    }
    CFRelease(childrenValue);
}

static NSString *accessible_controls_for_pid(pid_t pid) {
    if (!AXIsProcessTrusted()) return @"";
    AXUIElementRef application = AXUIElementCreateApplication(pid);
    if (!application) return @"";
    AXUIElementSetMessagingTimeout(application, 0.25f);
    NSMutableArray<NSString *> *names = [NSMutableArray array];
    NSUInteger visited = 0;
    CFTypeRef windowsValue = NULL;
    if (AXUIElementCopyAttributeValue(application, kAXWindowsAttribute, &windowsValue) == kAXErrorSuccess
        && windowsValue
        && CFGetTypeID(windowsValue) == CFArrayGetTypeID()) {
        NSArray *windows = (__bridge NSArray *)windowsValue;
        NSUInteger count = MIN(windows.count, 4);
        for (NSUInteger index = 0; index < count && visited < 64; index++) {
            id window = windows[index];
            if (CFGetTypeID((__bridge CFTypeRef)window) != AXUIElementGetTypeID()) continue;
            append_accessible_control_names((__bridge AXUIElementRef)window, names, &visited, 0);
        }
    }
    if (windowsValue) CFRelease(windowsValue);
    CFRelease(application);
    return [[NSOrderedSet orderedSetWithArray:names].array componentsJoinedByString:@"\n"];
}

static void append_accessible_window_title(AXUIElementRef window, NSMutableArray<NSString *> *titles) {
    if (!window || titles.count >= 8) return;
    CFTypeRef titleValue = NULL;
    if (AXUIElementCopyAttributeValue(window, kAXTitleAttribute, &titleValue) == kAXErrorSuccess
        && titleValue
        && CFGetTypeID(titleValue) == CFStringGetTypeID()) {
        NSString *title = (__bridge NSString *)titleValue;
        if (title.length > 0 && ![titles containsObject:title]) [titles addObject:title];
    }
    if (titleValue) CFRelease(titleValue);
}

static void append_accessible_string_attribute(AXUIElementRef element, CFStringRef attribute,
                                               NSMutableArray<NSString *> *titles) {
    if (!element || titles.count >= 8) return;
    CFTypeRef value = NULL;
    if (AXUIElementCopyAttributeValue(element, attribute, &value) == kAXErrorSuccess
        && value
        && CFGetTypeID(value) == CFStringGetTypeID()) {
        NSString *text = (__bridge NSString *)value;
        if (text.length > 0 && ![titles containsObject:text]) [titles addObject:text];
    }
    if (value) CFRelease(value);
}

static void append_accessible_document_titles(AXUIElementRef element, NSMutableArray<NSString *> *titles,
                                              NSUInteger *visited, NSUInteger depth) {
    if (!element || titles.count >= 8 || *visited >= 64 || depth > 6) return;
    (*visited)++;

    CFTypeRef roleValue = NULL;
    if (AXUIElementCopyAttributeValue(element, kAXRoleAttribute, &roleValue) == kAXErrorSuccess
        && roleValue
        && CFGetTypeID(roleValue) == CFStringGetTypeID()) {
        NSString *role = (__bridge NSString *)roleValue;
        if ([role isEqualToString:@"AXWebArea"] || [role isEqualToString:@"AXDocument"]) {
            append_accessible_string_attribute(element, kAXTitleAttribute, titles);
            append_accessible_string_attribute(element, kAXDescriptionAttribute, titles);
        }
    }
    if (roleValue) CFRelease(roleValue);

    CFTypeRef childrenValue = NULL;
    if (AXUIElementCopyAttributeValue(element, kAXChildrenAttribute, &childrenValue) != kAXErrorSuccess
        || !childrenValue) return;
    if (CFGetTypeID(childrenValue) == CFArrayGetTypeID()) {
        NSArray *children = (__bridge NSArray *)childrenValue;
        for (id child in children) {
            if (CFGetTypeID((__bridge CFTypeRef)child) != AXUIElementGetTypeID()) continue;
            append_accessible_document_titles((__bridge AXUIElementRef)child, titles, visited, depth + 1);
            if (*visited >= 64 || titles.count >= 8) break;
        }
    }
    CFRelease(childrenValue);
}

static NSString *accessible_window_title_for_pid(pid_t pid) {
    if (!AXIsProcessTrusted()) return @"";
    AXUIElementRef application = AXUIElementCreateApplication(pid);
    if (!application) return @"";
    AXUIElementSetMessagingTimeout(application, 0.25f);

    NSMutableArray<NSString *> *titles = [NSMutableArray array];
    NSUInteger visited = 0;
    CFTypeRef windowValue = NULL;
    if (AXUIElementCopyAttributeValue(application, kAXFocusedWindowAttribute, &windowValue) == kAXErrorSuccess
        && windowValue
        && CFGetTypeID(windowValue) == AXUIElementGetTypeID()) {
        append_accessible_window_title((AXUIElementRef)windowValue, titles);
        append_accessible_document_titles((AXUIElementRef)windowValue, titles, &visited, 0);
    }
    if (windowValue) CFRelease(windowValue);

    CFTypeRef windowsValue = NULL;
    if (AXUIElementCopyAttributeValue(application, kAXWindowsAttribute, &windowsValue) == kAXErrorSuccess
        && windowsValue
        && CFGetTypeID(windowsValue) == CFArrayGetTypeID()) {
        CFIndex count = MIN(CFArrayGetCount(windowsValue), 8);
        for (CFIndex index = 0; index < count; index++) {
            CFTypeRef window = CFArrayGetValueAtIndex(windowsValue, index);
            if (window && CFGetTypeID(window) == AXUIElementGetTypeID()) {
                append_accessible_window_title((AXUIElementRef)window, titles);
                append_accessible_document_titles((AXUIElementRef)window, titles, &visited, 0);
            }
        }
    }
    if (windowsValue) CFRelease(windowsValue);
    CFRelease(application);
    return [titles componentsJoinedByString:@"\n"];
}

__attribute__((visibility("default")))
int32_t ist_accessible_control_names(int32_t pid, char *destination, int32_t capacity) {
    if (!destination || capacity <= 0 || pid <= 0) return 0;
    copy_utf8(accessible_controls_for_pid(pid), destination, (size_t)capacity);
    return (int32_t)strlen(destination);
}

__attribute__((visibility("default")))
int32_t ist_accessible_window_title(int32_t pid, char *destination, int32_t capacity) {
    if (!destination || capacity <= 0 || pid <= 0) return 0;
    copy_utf8(accessible_window_title_for_pid(pid), destination, (size_t)capacity);
    return (int32_t)strlen(destination);
}

static bool process_tree_contains_zoom_host(pid_t parent, int depth) {
    if (parent <= 0 || depth > 4) return false;
    char parent_name[PROC_PIDPATHINFO_MAXSIZE] = {0};
    proc_name(parent, parent_name, sizeof(parent_name));
    if (strcasecmp(parent_name, "CptHost") == 0 || strcasecmp(parent_name, "caphost") == 0) return true;
    pid_t children[128] = {0};
    int count = proc_listchildpids(parent, children, (int)sizeof(children));
    if (count <= 0) return false;
    count = MIN(count, 128);
    for (int index = 0; index < count; index++) {
        pid_t child = children[index];
        if (child <= 0) continue;
        char name[PROC_PIDPATHINFO_MAXSIZE] = {0};
        proc_name(child, name, sizeof(name));
        if (strcasecmp(name, "CptHost") == 0 || strcasecmp(name, "caphost") == 0) return true;
        if (process_tree_contains_zoom_host(child, depth + 1)) return true;
    }
    return false;
}

__attribute__((visibility("default")))
int32_t ist_zoom_meeting_host_active(int32_t root_pid) {
    return process_tree_contains_zoom_host((pid_t)root_pid, 0) ? 1 : 0;
}

__attribute__((visibility("default")))
int32_t ist_accessibility_permission(void) {
    return permission_state_with_request_history(AXIsProcessTrusted(), ist_accessibility_requested_key);
}

__attribute__((visibility("default")))
int32_t ist_screen_capture_permission(void) {
    return permission_state_with_request_history(CGPreflightScreenCaptureAccess(), ist_screen_capture_requested_key);
}

__attribute__((visibility("default")))
int32_t ist_login_item_status(void) {
    if (@available(macOS 13.0, *)) return (int32_t)SMAppService.mainAppService.status;
    return 3;
}

__attribute__((visibility("default")))
int32_t ist_set_login_item_enabled(uint8_t enabled) {
    if (@available(macOS 13.0, *)) {
        NSError *error = nil;
        BOOL changed = enabled
            ? [SMAppService.mainAppService registerAndReturnError:&error]
            : [SMAppService.mainAppService unregisterAndReturnError:&error];
        return changed ? 0 : (int32_t)(error.code == 0 ? -1 : error.code);
    }
    return -2;
}

__attribute__((visibility("default")))
int32_t ist_request_microphone_permission(void) {
    if (@available(macOS 14.0, *)) {
        dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
        __block BOOL granted = NO;
        [AVAudioApplication requestRecordPermissionWithCompletionHandler:^(BOOL allowed) {
            granted = allowed;
            dispatch_semaphore_signal(semaphore);
        }];
        if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC)) != 0) return -1;
        return granted ? ist_permission_granted : ist_permission_denied;
    }
    return ist_permission_undetermined;
}

static BOOL probe_system_audio_permission(void) {
    if (@available(macOS 14.2, *)) {
        CATapDescription *description = [[CATapDescription alloc] initStereoGlobalTapButExcludeProcesses:@[]];
        description.name = @"isTranscribe permission probe";
        description.privateTap = YES;
        description.muteBehavior = CATapUnmuted;
        AudioObjectID tap = kAudioObjectUnknown;
        OSStatus status = AudioHardwareCreateProcessTap(description, &tap);
        if (status != noErr) return NO;
        AudioHardwareDestroyProcessTap(tap);
        return YES;
    }
    return NO;
}

__attribute__((visibility("default")))
int32_t ist_system_audio_permission(void) {
    if (![NSUserDefaults.standardUserDefaults boolForKey:ist_system_audio_requested_key]) {
        return ist_permission_undetermined;
    }
    return probe_system_audio_permission() ? ist_permission_granted : ist_permission_denied;
}

__attribute__((visibility("default")))
int32_t ist_request_system_audio_permission(void) {
    mark_permission_requested(ist_system_audio_requested_key);
    return probe_system_audio_permission() ? ist_permission_granted : ist_permission_denied;
}

__attribute__((visibility("default")))
int32_t ist_request_screen_capture_permission(void) {
    mark_permission_requested(ist_screen_capture_requested_key);
    return CGRequestScreenCaptureAccess() ? ist_permission_granted : ist_permission_denied;
}

__attribute__((visibility("default")))
int32_t ist_request_accessibility_permission(void) {
    mark_permission_requested(ist_accessibility_requested_key);
    NSDictionary *options = @{ (__bridge NSString *)kAXTrustedCheckOptionPrompt: @YES };
    return AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options)
        ? ist_permission_granted : ist_permission_denied;
}

static NSDictionary *keychain_query(NSString *account) {
    return @{ (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
              (__bridge id)kSecAttrService: @"com.istranscribe.app",
              (__bridge id)kSecAttrAccount: account };
}

__attribute__((visibility("default")))
int32_t ist_keychain_read(const char *account_utf8, uint8_t *destination, int32_t capacity) {
    if (!account_utf8) return errSecParam;
    NSString *account = [NSString stringWithUTF8String:account_utf8];
    NSMutableDictionary *query = [keychain_query(account) mutableCopy];
    query[(__bridge id)kSecReturnData] = @YES;
    query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
    CFTypeRef result = NULL;
    OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
    if (status != errSecSuccess) return status;
    NSData *data = CFBridgingRelease(result);
    if (!destination || capacity == 0) return (int32_t)data.length;
    if (capacity < data.length) return errSecBufferTooSmall;
    memcpy(destination, data.bytes, data.length);
    return (int32_t)data.length;
}

__attribute__((visibility("default")))
int32_t ist_keychain_write(const char *account_utf8, const uint8_t *value, int32_t length) {
    if (!account_utf8 || !value || length < 0) return errSecParam;
    NSString *account = [NSString stringWithUTF8String:account_utf8];
    NSData *data = [NSData dataWithBytes:value length:(NSUInteger)length];
    NSDictionary *query = keychain_query(account);
    OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query,
                                    (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData: data});
    if (status == errSecItemNotFound) {
        NSMutableDictionary *item = [query mutableCopy];
        item[(__bridge id)kSecValueData] = data;
        status = SecItemAdd((__bridge CFDictionaryRef)item, NULL);
    }
    return status;
}

__attribute__((visibility("default")))
int32_t ist_keychain_delete(const char *account_utf8) {
    if (!account_utf8) return errSecParam;
    OSStatus status = SecItemDelete((__bridge CFDictionaryRef)keychain_query([NSString stringWithUTF8String:account_utf8]));
    return status == errSecItemNotFound ? errSecSuccess : status;
}

__attribute__((visibility("default")))
int32_t ist_notification_permission(void) {
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
    __block NSInteger authorization = 0;
    [UNUserNotificationCenter.currentNotificationCenter getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) {
        authorization = settings.authorizationStatus;
        dispatch_semaphore_signal(semaphore);
    }];
    if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC)) != 0) return -1;
    if (authorization == UNAuthorizationStatusAuthorized || authorization == UNAuthorizationStatusProvisional) return ist_permission_granted;
    if (authorization == UNAuthorizationStatusDenied) return ist_permission_denied;
    return ist_permission_undetermined;
}

__attribute__((visibility("default")))
int32_t ist_request_notification_permission(void) {
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
    __block BOOL granted = NO;
    [UNUserNotificationCenter.currentNotificationCenter requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound)
                                                                      completionHandler:^(BOOL allowed, NSError *error) {
        granted = allowed && error == nil;
        dispatch_semaphore_signal(semaphore);
    }];
    if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC)) != 0) return -1;
    return granted ? ist_permission_granted : ist_permission_denied;
}

__attribute__((visibility("default")))
int32_t ist_show_notification(const char *title_utf8, const char *message_utf8) {
    if (!title_utf8 || !message_utf8) return -1;
    UNMutableNotificationContent *content = [UNMutableNotificationContent new];
    content.title = [NSString stringWithUTF8String:title_utf8];
    content.body = [NSString stringWithUTF8String:message_utf8];
    content.sound = UNNotificationSound.defaultSound;
    UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:NSUUID.UUID.UUIDString
                                                                           content:content trigger:nil];
    [UNUserNotificationCenter.currentNotificationCenter addNotificationRequest:request withCompletionHandler:nil];
    return 0;
}

// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
__attribute__((visibility("default")))
int32_t ist_present_ask_prompt_window(void *window_handle) {
    if (!window_handle) return -1;
    if (![NSThread isMainThread]) return -2;

    NSWindow *window = (__bridge NSWindow *)window_handle;
    window.collectionBehavior |= NSWindowCollectionBehaviorCanJoinAllSpaces
        | NSWindowCollectionBehaviorFullScreenAuxiliary;
    window.level = NSFloatingWindowLevel;
    [window orderFrontRegardless];
    return 0;
}

__attribute__((visibility("default")))
int32_t ist_enumerate_windows(ist_window *destination, int32_t capacity, uint8_t include_accessibility) {
    // @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
    // Keep meeting evidence stable when its layer-zero window is on another macOS Space.
    CFArrayRef windowInfo = CGWindowListCopyWindowInfo(
        kCGWindowListOptionAll | kCGWindowListExcludeDesktopElements,
        kCGNullWindowID);
    if (!windowInfo) return 0;
    NSArray *windows = CFBridgingRelease(windowInfo);
    pid_t foregroundPid = NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier;
    int32_t eligible = 0;
    int32_t written = 0;
    NSMutableDictionary<NSNumber *, NSString *> *controlsByPid = [NSMutableDictionary dictionary];
    for (NSDictionary *window in windows) {
        NSNumber *layer = window[(id)kCGWindowLayer];
        NSNumber *pid = window[(id)kCGWindowOwnerPID];
        NSNumber *windowId = window[(id)kCGWindowNumber];
        NSString *owner = window[(id)kCGWindowOwnerName] ?: @"";
        NSString *title = window[(id)kCGWindowName] ?: @"";
        if (!pid || layer.integerValue != 0 || (title.length == 0 && owner.length == 0)) continue;
        eligible++;
        if (!destination || written >= capacity) continue;
        NSString *controls = @"";
        if (include_accessibility) {
            controls = controlsByPid[pid];
            if (!controls) {
                controls = accessible_controls_for_pid(pid.intValue);
                controlsByPid[pid] = controls;
            }
        }
        destination[written].pid = pid.intValue;
        destination[written].window_id = windowId.unsignedIntValue;
        destination[written].is_foreground = pid.intValue == foregroundPid;
        copy_utf8(owner, destination[written].owner_name, 256);
        copy_utf8(title, destination[written].title, 512);
        copy_utf8(controls, destination[written].accessible_controls, 2048);
        written++;
    }
    return destination == NULL ? eligible : written;
}

__attribute__((visibility("default")))
uint8_t ist_foreground_window_center(int32_t *x, int32_t *y) {
    pid_t foregroundPid = NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier;
    CFArrayRef windowInfo = CGWindowListCopyWindowInfo(
        kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements,
        kCGNullWindowID);
    if (!windowInfo) return 0;
    NSArray *windows = CFBridgingRelease(windowInfo);
    for (NSDictionary *window in windows) {
        NSNumber *pid = window[(id)kCGWindowOwnerPID];
        NSNumber *layer = window[(id)kCGWindowLayer];
        NSDictionary *bounds = window[(id)kCGWindowBounds];
        CGRect rectangle = CGRectZero;
        if (pid.intValue != foregroundPid || layer.integerValue != 0 || !bounds
            || !CGRectMakeWithDictionaryRepresentation((__bridge CFDictionaryRef)bounds, &rectangle)) continue;
        if (x) *x = (int32_t)llround(CGRectGetMidX(rectangle));
        if (y) *y = (int32_t)llround(CGRectGetMidY(rectangle));
        return 1;
    }
    return 0;
}

static NSNumber *process_object_for_pid(int32_t pid) {
    for (NSNumber *number in object_list(kAudioHardwarePropertyProcessObjectList)) {
        pid_t candidate = 0;
        if (read_value(number.unsignedIntValue, kAudioProcessPropertyPID, kAudioObjectPropertyScopeGlobal,
                       &candidate, sizeof(candidate)) && candidate == pid) return number;
    }
    return nil;
}

static AudioObjectID device_object_for_uid(NSString *uid) {
    for (NSNumber *number in object_list(kAudioHardwarePropertyDevices)) {
        if ([read_string(number.unsignedIntValue, kAudioDevicePropertyDeviceUID) isEqualToString:uid]) {
            return number.unsignedIntValue;
        }
    }
    return kAudioObjectUnknown;
}

static void deliver_pcm(const AudioBufferList *input, const AudioStreamBasicDescription *format,
                        uint64_t host_time, ist_pcm_callback callback, void *context) {
    if (!input || input->mNumberBuffers == 0 || format->mFormatID != kAudioFormatLinearPCM
        || !(format->mFormatFlags & kAudioFormatFlagIsFloat)
        || format->mBitsPerChannel != 32) return;
    bool nonInterleaved = (format->mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0;
    if (!nonInterleaved) {
        const AudioBuffer buffer = input->mBuffers[0];
        uint32_t channels = MAX(buffer.mNumberChannels, 1);
        uint32_t frames = buffer.mDataByteSize / (sizeof(float) * channels);
        if (buffer.mData && frames) callback(context, buffer.mData, frames, channels, format->mSampleRate, host_time);
        return;
    }

    uint32_t channels = input->mNumberBuffers;
    uint32_t frames = input->mBuffers[0].mDataByteSize / sizeof(float);
    if (channels == 0 || frames == 0) return;
    NSMutableData *interleaved = [NSMutableData dataWithLength:(NSUInteger)frames * channels * sizeof(float)];
    float *target = interleaved.mutableBytes;
    for (uint32_t channel = 0; channel < channels; channel++) {
        const float *source = input->mBuffers[channel].mData;
        if (!source) return;
        for (uint32_t frame = 0; frame < frames; frame++) target[(NSUInteger)frame * channels + channel] = source[frame];
    }
    callback(context, target, frames, channels, format->mSampleRate, host_time);
}

__attribute__((visibility("default")))
void *ist_start_process_capture(int32_t pid, ist_pcm_callback callback, void *context, int32_t *status) {
    if (status) *status = kAudioHardwareBadObjectError;
    if (@available(macOS 14.2, *)) {
        NSNumber *processObject = pid > 0 ? process_object_for_pid(pid) : nil;
        if ((pid > 0 && !processObject) || !callback) return NULL;
        CATapDescription *description = pid > 0
            ? [[CATapDescription alloc] initStereoMixdownOfProcesses:@[processObject]]
            : [[CATapDescription alloc] initStereoGlobalTapButExcludeProcesses:@[]];
        description.name = pid > 0
            ? [NSString stringWithFormat:@"IsTranscribe process %d", pid]
            : @"IsTranscribe system output";
        description.privateTap = YES;
        description.muteBehavior = CATapUnmuted;
        AudioObjectID tap = kAudioObjectUnknown;
        OSStatus result = AudioHardwareCreateProcessTap(description, &tap);
        if (result != noErr) { if (status) *status = result; return NULL; }

        NSString *aggregateUID = NSUUID.UUID.UUIDString;
        NSDictionary *tapEntry = @{ @kAudioSubTapUIDKey: description.UUID.UUIDString,
                                    @kAudioSubTapDriftCompensationKey: @YES };
        NSDictionary *aggregateDescription = @{ @kAudioAggregateDeviceNameKey: @"IsTranscribe capture",
                                                @kAudioAggregateDeviceUIDKey: aggregateUID,
                                                @kAudioAggregateDeviceTapListKey: @[tapEntry],
                                                @kAudioAggregateDeviceIsPrivateKey: @YES };
        AudioObjectID aggregate = kAudioObjectUnknown;
        result = AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)aggregateDescription, &aggregate);
        if (result != noErr) {
            AudioHardwareDestroyProcessTap(tap);
            if (status) *status = result;
            return NULL;
        }

        AudioStreamBasicDescription format = {0};
        if (!read_value(tap, kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, &format, sizeof(format))) {
            AudioHardwareDestroyAggregateDevice(aggregate);
            AudioHardwareDestroyProcessTap(tap);
            return NULL;
        }
        ISTCapture *capture = [ISTCapture new];
        capture.tapID = tap; capture.aggregateID = aggregate; capture.callback = callback; capture.context = context;
        AudioDeviceIOProcID ioProc = NULL;
        result = AudioDeviceCreateIOProcIDWithBlock(&ioProc, aggregate, NULL,
            ^(const AudioTimeStamp *now, const AudioBufferList *input, const AudioTimeStamp *inputTime,
              AudioBufferList *output, const AudioTimeStamp *outputTime) {
                (void)now; (void)output; (void)outputTime;
                deliver_pcm(input, &format, inputTime ? inputTime->mHostTime : 0, callback, context);
            });
        if (result == noErr) result = AudioDeviceStart(aggregate, ioProc);
        if (result != noErr) {
            if (ioProc) AudioDeviceDestroyIOProcID(aggregate, ioProc);
            AudioHardwareDestroyAggregateDevice(aggregate);
            AudioHardwareDestroyProcessTap(tap);
            if (status) *status = result;
            return NULL;
        }
        capture.ioProcID = ioProc;
        if (status) *status = noErr;
        return (void *)CFBridgingRetain(capture);
    }
    return NULL;
}

__attribute__((visibility("default")))
void *ist_start_system_capture(ist_pcm_callback callback, void *context, int32_t *status) {
    return ist_start_process_capture(0, callback, context, status);
}

__attribute__((visibility("default")))
void *ist_start_microphone_capture(const char *device_uid, ist_pcm_callback callback, void *context, int32_t *status) {
    if (status) *status = -1;
    if (!callback || ist_audio_permission() != ist_permission_granted) return NULL;
    ISTCapture *capture = [ISTCapture new];
    capture.callback = callback; capture.context = context; capture.engine = [AVAudioEngine new];
    AVAudioInputNode *input = capture.engine.inputNode;
    NSString *uid = device_uid ? [NSString stringWithUTF8String:device_uid] : @"";
    AudioObjectID device = device_object_for_uid(uid);
    if (device == kAudioObjectUnknown || input.audioUnit == NULL) {
        if (status) *status = kAudioHardwareBadDeviceError;
        return NULL;
    }
    OSStatus selectResult = AudioUnitSetProperty(input.audioUnit, kAudioOutputUnitProperty_CurrentDevice,
                                                  kAudioUnitScope_Global, 0, &device, sizeof(device));
    if (selectResult != noErr) {
        if (status) *status = selectResult;
        return NULL;
    }
    AVAudioFormat *format = [input outputFormatForBus:0];
    [input installTapOnBus:0 bufferSize:1024 format:format block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
        if (!buffer.floatChannelData || buffer.frameLength == 0) return;
        uint32_t channels = buffer.format.channelCount;
        uint32_t frames = buffer.frameLength;
        NSMutableData *interleaved = [NSMutableData dataWithLength:(NSUInteger)frames * channels * sizeof(float)];
        float *target = interleaved.mutableBytes;
        for (uint32_t frame = 0; frame < frames; frame++)
            for (uint32_t channel = 0; channel < channels; channel++)
                target[(NSUInteger)frame * channels + channel] = buffer.floatChannelData[channel][frame];
        callback(context, target, frames, channels, format.sampleRate, when.hostTime);
    }];
    NSError *error = nil;
    if (![capture.engine startAndReturnError:&error]) {
        [input removeTapOnBus:0];
        if (status) *status = (int32_t)error.code;
        return NULL;
    }
    if (status) *status = noErr;
    return (void *)CFBridgingRetain(capture);
}

__attribute__((visibility("default")))
void ist_stop_capture(void *handle) {
    if (!handle) return;
    ISTCapture *capture = CFBridgingRelease(handle);
    if (capture.engine) {
        [capture.engine.inputNode removeTapOnBus:0];
        [capture.engine stop];
        capture.engine = nil;
    }
    if (capture.aggregateID != kAudioObjectUnknown && capture.ioProcID) {
        AudioDeviceStop(capture.aggregateID, capture.ioProcID);
        AudioDeviceDestroyIOProcID(capture.aggregateID, capture.ioProcID);
    }
    if (capture.aggregateID != kAudioObjectUnknown) AudioHardwareDestroyAggregateDevice(capture.aggregateID);
    if (capture.tapID != kAudioObjectUnknown) AudioHardwareDestroyProcessTap(capture.tapID);
}

__attribute__((visibility("default")))
int32_t ist_probe_audio(const char *path, double *sample_rate, uint32_t *channels,
                        double *duration_seconds, uint32_t *format_id, uint32_t *bitrate) {
    if (!path) return paramErr;
    NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
    AudioFileID file = NULL;
    OSStatus result = AudioFileOpenURL((__bridge CFURLRef)url, kAudioFileReadPermission, 0, &file);
    if (result != noErr) return result;
    AudioStreamBasicDescription format = {0};
    UInt32 size = sizeof(format);
    result = AudioFileGetProperty(file, kAudioFilePropertyDataFormat, &size, &format);
    UInt64 packetCount = 0;
    size = sizeof(packetCount);
    if (result == noErr) result = AudioFileGetProperty(file, kAudioFilePropertyAudioDataPacketCount, &size, &packetCount);
    if (result == noErr) {
        if (sample_rate) *sample_rate = format.mSampleRate;
        if (channels) *channels = format.mChannelsPerFrame;
        if (format_id) *format_id = format.mFormatID;
        if (duration_seconds) *duration_seconds = format.mSampleRate > 0
            ? (double)(packetCount * format.mFramesPerPacket) / format.mSampleRate : 0;
        if (bitrate) {
            size = sizeof(*bitrate);
            if (AudioFileGetProperty(file, kAudioFilePropertyBitRate, &size, bitrate) != noErr) *bitrate = 0;
        }
    }
    AudioFileClose(file);
    return result;
}

__attribute__((visibility("default")))
int32_t ist_measure_tone(const char *path, double frequency, double *amplitude) {
    if (!path || frequency <= 0 || !amplitude) return paramErr;
    NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
    ExtAudioFileRef file = NULL;
    OSStatus result = ExtAudioFileOpenURL((__bridge CFURLRef)url, &file);
    if (result != noErr) return result;
    AudioStreamBasicDescription client = {0};
    client.mSampleRate = 48000;
    client.mFormatID = kAudioFormatLinearPCM;
    client.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    client.mBytesPerPacket = 2 * sizeof(float);
    client.mFramesPerPacket = 1;
    client.mBytesPerFrame = 2 * sizeof(float);
    client.mChannelsPerFrame = 2;
    client.mBitsPerChannel = 32;
    result = ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat, sizeof(client), &client);
    if (result != noErr) { ExtAudioFileDispose(file); return result; }

    const UInt32 capacity = 4096;
    float *samples = calloc((size_t)capacity * 2, sizeof(float));
    if (!samples) { ExtAudioFileDispose(file); return memFullErr; }
    AudioBufferList buffers = { .mNumberBuffers = 1,
                                .mBuffers = {{ .mNumberChannels = 2,
                                               .mDataByteSize = capacity * 2 * sizeof(float),
                                               .mData = samples }} };
    double real = 0, imaginary = 0;
    uint64_t sampleIndex = 0;
    do {
        UInt32 frames = capacity;
        buffers.mBuffers[0].mDataByteSize = capacity * 2 * sizeof(float);
        result = ExtAudioFileRead(file, &frames, &buffers);
        if (result != noErr || frames == 0) break;
        for (UInt32 frame = 0; frame < frames; frame++, sampleIndex++) {
            double mono = ((double)samples[frame * 2] + samples[frame * 2 + 1]) * 0.5;
            double phase = 2.0 * M_PI * frequency * (double)sampleIndex / client.mSampleRate;
            real += mono * cos(phase);
            imaginary -= mono * sin(phase);
        }
    } while (result == noErr);
    free(samples);
    ExtAudioFileDispose(file);
    if (result == noErr && sampleIndex > 0) *amplitude = 2.0 * hypot(real, imaginary) / (double)sampleIndex;
    return result;
}
