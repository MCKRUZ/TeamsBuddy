// Teams Authentication JavaScript Interop
// Provides functions for Teams SSO and context retrieval

window.teamsAuth = {
    isInitialized: false,
    teamsContext: null,
    debugInfo: {
        sdkLoaded: false,
        initStarted: false,
        initCompleted: false,
        contextRetrieved: false,
        errors: []
    },

    /**
     * Get comprehensive debug information
     * @returns {object} Debug information
     */
    getDebugInfo: function () {
        const info = {
            timestamp: new Date().toISOString(),
            sdkStatus: {
                microsoftTeamsExists: typeof microsoftTeams !== 'undefined',
                sdkVersion: typeof microsoftTeams !== 'undefined' ? microsoftTeams.version : 'N/A',
                isInitialized: this.isInitialized
            },
            environment: {
                userAgent: navigator.userAgent,
                url: window.location.href,
                referrer: document.referrer,
                inIframe: window !== window.top
            },
            context: this.teamsContext ? {
                hasUser: !!this.teamsContext.user,
                userId: this.teamsContext.user?.id,
                hasMeeting: !!this.teamsContext.meeting,
                meetingId: this.teamsContext.meeting?.id,
                hasTenant: !!this.teamsContext.user?.tenant,
                tenantId: this.teamsContext.user?.tenant?.id,
                appSessionId: this.teamsContext.app?.sessionId,
                hostClientType: this.teamsContext.app?.host?.clientType
            } : null,
            debugInfo: this.debugInfo
        };

        console.log('=== TEAMS AUTH DEBUG INFO ===', JSON.stringify(info, null, 2));
        return info;
    },

    /**
     * Serialize error object to a string representation
     * @param {any} error - Error object to serialize
     * @returns {string} Serialized error
     */
    serializeError: function (error) {
        if (typeof error === 'string') {
            return error;
        }
        
        if (error instanceof Error) {
            return JSON.stringify({
                name: error.name,
                message: error.message,
                stack: error.stack
            });
        }
        
        try {
            // Try to stringify the entire object
            return JSON.stringify(error, null, 2);
        } catch (e) {
            // If JSON.stringify fails, try to extract key properties
            const errorObj = {
                type: typeof error,
                errorCode: error?.errorCode,
                message: error?.message,
                error: error?.error,
                toString: error?.toString()
            };
            return JSON.stringify(errorObj, null, 2);
        }
    },

    /**
     * Check if running inside Teams
     * @returns {boolean}
     */
    isInTeams: function () {
        const result = typeof microsoftTeams !== 'undefined';
        console.log('[DEBUG] isInTeams check:', result);
        this.debugInfo.sdkLoaded = result;
        return result;
    },

    /**
     * Initialize Microsoft Teams SDK
     * @returns {Promise<object>} Teams context object
     */
    initialize: async function () {
        console.log('[DEBUG] ========== TEAMS INITIALIZATION STARTED ==========');
        console.log('[DEBUG] Current URL:', window.location.href);
        console.log('[DEBUG] In iframe:', window !== window.top);
        console.log('[DEBUG] Referrer:', document.referrer);

        return new Promise((resolve, reject) => {
            try {
                this.debugInfo.initStarted = true;

                // Check if we're running inside Teams
                if (typeof microsoftTeams === 'undefined') {
                    const error = 'Microsoft Teams SDK not loaded - running in standalone mode';
                    console.error('[DEBUG] FATAL:', error);
                    console.log('[DEBUG] Available global objects:', Object.keys(window).filter(k => k.includes('teams') || k.includes('Teams')));
                    this.debugInfo.errors.push({ timestamp: new Date().toISOString(), error });
                    reject({ error: 'not_in_teams', message: 'Not running inside Microsoft Teams' });
                    return;
                }

                console.log('[DEBUG] Teams SDK detected:', {
                    version: microsoftTeams.version,
                    appExists: typeof microsoftTeams.app !== 'undefined',
                    authExists: typeof microsoftTeams.authentication !== 'undefined'
                });

                console.log('[DEBUG] Calling microsoftTeams.app.initialize()...');
                microsoftTeams.app.initialize().then(() => {
                    console.log('[DEBUG] ✅ Teams SDK initialized successfully');
                    this.debugInfo.initCompleted = true;
                    console.log('[DEBUG] Calling microsoftTeams.app.getContext()...');
                    // Get Teams context
                    microsoftTeams.app.getContext().then((context) => {
                        console.log('[DEBUG] ✅ Teams context retrieved successfully');
                        this.debugInfo.contextRetrieved = true;
                        console.log("App ID:", context);

                        console.log('[DEBUG] Context details:', {
                            raw: context,
                            user: {
                                exists: !!context.user,
                                id: context.user?.id,
                                userPrincipalName: context.user?.userPrincipalName,
                                displayName: context.user?.displayName,
                                licenseType: context.user?.licenseType
                            },
                            meeting: {
                                exists: !!context.meeting,
                                id: context.meeting?.id
                            },
                            app: {
                                sessionId: context.app?.sessionId,
                                locale: context.app?.locale,
                                theme: context.app?.theme,
                                hostClientType: context.app?.host?.clientType,
                                hostName: context.app?.host?.name
                            },
                            page: {
                                id: context.page?.id,
                                frameContext: context.page?.frameContext,
                                subPageId: context.page?.subPageId
                            },
                            team: context.team ? {
                                internalId: context.team.internalId,
                                displayName: context.team.displayName
                            } : null,
                            channel: context.channel ? {
                                id: context.channel.id,
                                displayName: context.channel.displayName
                            } : null
                        });

                        this.isInitialized = true;
                        this.teamsContext = context;

                        console.log('[DEBUG] ========== TEAMS INITIALIZATION COMPLETED ==========');
                        resolve(context);
                    }).catch((error) => {
                        const errorStr = this.serializeError(error);
                        console.error('[DEBUG] ❌ Failed to get Teams context:', errorStr);
                        this.debugInfo.errors.push({ timestamp: new Date().toISOString(), stage: 'getContext', error: errorStr });
                        reject({ error: 'context_failed', message: errorStr });
                    });
                }).catch((error) => {
                    const errorStr = this.serializeError(error);
                    console.error('[DEBUG] ❌ Failed to initialize Teams SDK:', errorStr);
                    this.debugInfo.errors.push({ timestamp: new Date().toISOString(), stage: 'initialize', error: errorStr });
                    reject({ error: 'init_failed', message: errorStr });
                });
            } catch (error) {
                const errorStr = this.serializeError(error);
                console.error('[DEBUG] Exception during Teams initialization:', errorStr);
                this.debugInfo.errors.push({ timestamp: new Date().toISOString(), stage: 'exception', error: errorStr });
                reject({ error: 'exception', message: errorStr });
            }
        });
    },

    /**
     * Get SSO authentication token from Teams
     * @returns {Promise<string>} ID token
     */
    getAuthToken: async function () {
        console.log('[DEBUG] ========== SSO TOKEN REQUEST STARTED ==========');
        console.log('[DEBUG] Is initialized:', this.isInitialized);
        console.log('[DEBUG] Has context:', !!this.teamsContext);
        console.log('[DEBUG] Current window.location.origin:', window.location.origin);
        console.log('[DEBUG] Current window.location.hostname:', window.location.hostname);
        console.log('[DEBUG] Expected App ID URI format: api://' + window.location.hostname + '/[your-app-id]');
        
        return new Promise((resolve, reject) => {
            try {
                if (typeof microsoftTeams === 'undefined') {
                    const error = 'Microsoft Teams SDK not loaded - cannot get auth token';
                    console.error('[DEBUG] FATAL:', error);
                    reject({ error: 'not_in_teams', message: 'Not running inside Microsoft Teams' });
                    return;
                }

                console.log('[DEBUG] Requesting Teams SSO token...');
                console.log('[DEBUG] Authentication API exists:', typeof microsoftTeams.authentication !== 'undefined');

                const authTokenRequest = {
                    successCallback: (token) => {
                        console.log('[DEBUG] ✅ SSO token received successfully');
                        console.log('[DEBUG] Token length:', token.length);
                        console.log('[DEBUG] Token preview:', token.substring(0, 50) + '...');
                        
                        // Try to decode and display token info (for debugging)
                        try {
                            const parts = token.split('.');
                            if (parts.length === 3) {
                                const payload = JSON.parse(atob(parts[1]));
                                console.log('[DEBUG] Token payload:', {
                                    aud: payload.aud,
                                    iss: payload.iss,
                                    iat: payload.iat,
                                    exp: payload.exp,
                                    name: payload.name,
                                    preferred_username: payload.preferred_username,
                                    oid: payload.oid,
                                    tid: payload.tid
                                });
                            }
                        } catch (e) {
                            console.warn('[DEBUG] Could not decode token:', e);
                        }

                        console.log('[DEBUG] ========== SSO TOKEN REQUEST COMPLETED ==========');
                        resolve(token);
                    },
                    failureCallback: (error) => {
                        console.log(error);
                        // Serialize the error object properly
                        const errorStr = this.serializeError(error);
                        console.error('[DEBUG] ❌ Failed to get SSO token:', errorStr);
                        console.error('[DEBUG] Error type:', typeof error);
                        console.error('[DEBUG] Error keys:', error ? Object.keys(error) : 'null');
                        
                        this.debugInfo.errors.push({ 
                            timestamp: new Date().toISOString(), 
                            stage: 'getAuthToken', 
                            error: errorStr 
                        });
                        
                        // Build a detailed error object
                        let errorMessage = 'Failed to get authentication token';
                        let errorCode = 'unknown';
                        
                        if (typeof error === 'string') {
                            errorMessage = error;
                            errorCode = 'string_error';
                        } else if (error && typeof error === 'object') {
                            errorCode = error.errorCode || error.error || 'object_error';
                            errorMessage = error.message || errorStr;
                        }

                        console.log('[DEBUG] ========== SSO TOKEN REQUEST FAILED ==========');
                        console.log('[DEBUG] Error code:', errorCode);
                        console.log('[DEBUG] Error message:', errorMessage);
                        console.log('[DEBUG] Full error serialized:', errorStr);
                        
                        reject({ 
                            error: errorCode, 
                            message: errorMessage,
                            details: errorStr,
                            serialized: errorStr
                        });
                    }
                };

                console.log('[DEBUG] Calling microsoftTeams.authentication.getAuthToken()...');
                microsoftTeams.authentication.getAuthToken(authTokenRequest);
            } catch (error) {
                const errorStr = this.serializeError(error);
                console.error('[DEBUG] Exception during SSO token request:', errorStr);
                this.debugInfo.errors.push({ 
                    timestamp: new Date().toISOString(), 
                    stage: 'getAuthToken_exception', 
                    error: errorStr 
                });
                reject({ error: 'exception', message: errorStr, serialized: errorStr });
            }
        });
    },

    /**
     * Get the current Teams context
     * @returns {object|null} Teams context object
     */
    getContext: function () {
        console.log('[DEBUG] Getting cached Teams context');
        console.log('[DEBUG] Context available:', !!this.teamsContext);
        return this.teamsContext;
    },

    /**
     * Notify Teams that app has loaded successfully
     */
    notifySuccess: function () {
        if (typeof microsoftTeams !== 'undefined' && this.isInitialized) {
            console.log('[DEBUG] Notifying Teams of successful app load');
            microsoftTeams.app.notifySuccess();
        }
    },

    /**
     * Notify Teams that app has failed to load
     * @param {string} reason - Failure reason
     */
    notifyFailure: function (reason) {
        if (typeof microsoftTeams !== 'undefined') {
            console.error('[DEBUG] Notifying Teams of app load failure:', reason);
            microsoftTeams.app.notifyFailure({
                reason: microsoftTeams.app.FailedReason.Other,
                message: reason
            });
        }
    }
};

// Auto-initialize on page load if in Teams
console.log('[DEBUG] teams-auth.js loaded');
console.log('[DEBUG] Will check for Teams SDK on DOMContentLoaded');
