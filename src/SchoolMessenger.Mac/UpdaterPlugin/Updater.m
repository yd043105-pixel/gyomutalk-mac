#import <Cocoa/Cocoa.h>
#import <Sparkle/Sparkle.h>
#import <objc/message.h>

// AppKit plug-in is required by Sparkle's supported Mac Catalyst integration.
@interface GYUpdaterPlugin : NSObject <SPUUpdaterDelegate>
@property(strong) SPUStandardUpdaterController *controller;
@property(copy) void (^pendingInstall)(void);
@property(strong) NSTimer *waitTimer;
@end
@implementation GYUpdaterPlugin
- (instancetype)init {
    if((self=[super init])) {
        _controller=[[SPUStandardUpdaterController alloc] initWithStartingUpdater:NO updaterDelegate:self userDriverDelegate:nil];
    }
    return self;
}
- (void)startUpdater { [self.controller startUpdater]; }
- (void)checkForUpdates { [self.controller checkForUpdates:nil]; }
- (void)enableAutomaticChecks { self.controller.updater.automaticallyChecksForUpdates=YES; }
- (void)disableAutomaticChecks { self.controller.updater.automaticallyChecksForUpdates=NO; }
- (BOOL)canInstallNow {
    Class application=NSClassFromString(@"UIApplication");
    if(!application)return NO;
    id app=((id(*)(id,SEL))objc_msgSend)(application,sel_registerName("sharedApplication"));
    id delegate=((id(*)(id,SEL))objc_msgSend)(app,sel_registerName("delegate"));
    SEL selector=sel_registerName("canInstallGyomutalkUpdate");
    if(![delegate respondsToSelector:selector])return NO;
    return ((BOOL(*)(id,SEL))objc_msgSend)(delegate,selector);
}
- (BOOL)updater:(SPUUpdater *)updater shouldPostponeRelaunchForUpdate:(SUAppcastItem *)item untilInvokingBlock:(void (^)(void))installHandler {
    if([self canInstallNow])return NO;
    self.pendingInstall=installHandler;
    __weak GYUpdaterPlugin *weakSelf=self;
    self.waitTimer=[NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer *timer) {
        GYUpdaterPlugin *strongSelf=weakSelf;
        if(!strongSelf){[timer invalidate];return;}
        if([strongSelf canInstallNow]) {
            [timer invalidate];strongSelf.waitTimer=nil;
            void (^resume)(void)=strongSelf.pendingInstall;strongSelf.pendingInstall=nil;
            if(resume)resume();
        }
    }];
    NSAlert *notice=[NSAlert new];notice.messageText=@"업무 화면을 닫으면 업데이트합니다";
    notice.informativeText=@"작성 중인 메시지를 전송하거나 닫고 첫 화면으로 돌아오면 업데이트를 계속합니다.";
    [notice addButtonWithTitle:@"확인"];[notice runModal];
    return YES;
}
@end
