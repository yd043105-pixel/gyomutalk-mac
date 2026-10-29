#import <Cocoa/Cocoa.h>
#import <Sparkle/Sparkle.h>

// AppKit plug-in is required by Sparkle's supported Mac Catalyst integration.
@interface GYUpdaterPlugin : NSObject
@property(strong) SPUStandardUpdaterController *controller;
@end
@implementation GYUpdaterPlugin
- (instancetype)init {
    if((self=[super init])) {
        _controller=[[SPUStandardUpdaterController alloc] initWithStartingUpdater:NO updaterDelegate:nil userDriverDelegate:nil];
    }
    return self;
}
- (void)startUpdater { [self.controller startUpdater]; }
- (void)checkForUpdates { [self.controller checkForUpdates:nil]; }
- (void)enableAutomaticChecks { self.controller.updater.automaticallyChecksForUpdates=YES; }
- (void)disableAutomaticChecks { self.controller.updater.automaticallyChecksForUpdates=NO; }
@end
