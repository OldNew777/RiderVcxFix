# Frontend environment API — reviewed static findings

Build: RD-263.7307.258 (2026.3). Read-only installed artifact analysis. These are reviewed notes from javap, not a complete raw dump.

Interface com.jetbrains.rider.protocol.RiderBackendEnvProvider:
- default boolean isDynamic(): bytecode returns false.
- Iterable<kotlin.Pair<String, String>> getEnvironment().
- Companion EP constant is com.intellij.rider.protocol.hostEnvProvider.
- getNonDynamicEnvironment selects !provider.isDynamic(); getDynamicEnvironment selects provider.isDynamic(); each concatenates getEnvironment().
- EP XML dynamic=true means extension unloading support, separate from provider.isDynamic() env collection.

Environment ordering:
- ReSharperProcess private static h() returning Companion.AdditionalInfo: offsets 82–88 call getNonDynamicEnvironment and MapsKt.putAll; stores nonDynamicEnvVars.
- AdditionalInfo.getExtraEnvVars returns nonDynamicEnvVars + getDynamicEnvironment().
- ReSharperProcess ctor stores allExtraEnvVars, then calls start().
- ReSharperProcessBase.start: offset 606 getExtraEnvVars; 641 ProcessBuilder.environment; 676 Map.put; 2009 ProcessBuilder.start.
- Therefore provider env reaches the child before CLR starts.
- com.intellij.rider.lightweightBackend.RiderLightweightBackendProtocolManager extends ProtocolManager; constructor passes boolean true; parent normal startup creates the same ReSharperProcess.
- Verified separately via javap: com.intellij.openapi.extensions.PluginDescriptor.getPluginPath returns java.nio.file.Path; PluginManagerCore.getPlugin(PluginId) gets the descriptor.

EBS/cache behavior:
- EarlyBackendStarter.Companion.h() checks all three system/caches/rider_ebs_plugins.txt, rider_ebs_flags.txt, rider_ebs_vars.txt; if any missing, no EBS.
- Initial EBS reads cached vars, not current provider directly.
- EarlyBackendStarter ctor reads vars as newline-separated key=value into map, passes map to EarlyReSharperProcess; its getExtraEnvVars returns that map.
- ProtocolManager.h(EbsPreparedThings,Lifetime): current AdditionalInfo.getExtraEnvVars at 126; cached process env at 169; equality check 211–218; mismatch terminates EBS lifetime at 231–241, logs restart at 245, records killedEbs at 392–398, requestShutdown at 403, destroyProcess at 411, returns null at 414–415.
- Caller ProtocolManager private ProtocolHost h(Lifetime): null early result at 25 branches to normal at 59; constructs ReSharperProcess using current env at 115–143.
- First full IDE restart after enabling provider can discard stale unhooked EBS and replace it in the same IDE start; no second manual IDE restart is implied by this path.
- RiderEBSPluginCacheHandler.appWillBeClosed writes caches; private h() gets AdditionalInfo.getExtraEnvVars at 42 and writes rider_ebs_vars.txt at 205–242.
- NonDynamic map is lazy/captured for the frontend lifetime; disable/uninstall can leave old hook env in the exit cache.

Uninstall caveat:
- EbsPreparedThings catches Throwable and completeExceptionally; getValue calls CompletableFuture.get() with no explicit timeout.
- ProtocolManager catches Throwable around getValue; getOrLogException and null result return null; caller follows normal fallback.
- But OS spawn can succeed and CLR can exit due to a missing startup hook before handshake. Base.start does not wait for CLR handshake before EBS future completion.
- The unexpected-exit path may enqueue a critical dialog before stale EBS cleanup. The supplied lifecycle snippets show no early-process exemption and no later lifetime/requestShutdown recheck.
- Cannot statically guarantee clean uninstall after DLL deletion with stale hook env; runtime verification / cache-lifecycle design is required.
- fullBackendShutdownTimeout is Duration.ofMinutes(1).toMillis() = 60000 ms; lightweight is Duration.ofSeconds(1).toMillis() = 1000 ms. These are shutdown waits, not startup/EBS recovery timeout.