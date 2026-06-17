let currentSession = null;

export async function startAndSpeak(dotNetReference, videoElement, audioElement, text) {
    if (!text || !text.trim()) {
        throw new Error('Enter text for the avatar to speak.');
    }

    const session = currentSession || await startSession(dotNetReference, videoElement, audioElement);
    await invoke(session, 'OnAvatarStatus', 'Sending text to avatar...');
    await invoke(session, 'SpeakAvatarTextAsync', text);
}

export async function startSession(dotNetReference, videoElement, audioElement) {
    if (currentSession) {
        await invoke(currentSession, 'OnAvatarStatus', 'Avatar session already running.');
        return currentSession;
    }

    const session = {
        dotNetReference,
        videoElement,
        audioElement,
        peerConnection: null
    };

    currentSession = session;

    try {
        await invoke(session, 'OnAvatarStatus', 'Fetching avatar relay token...');
        const iceResponse = await invoke(session, 'GetAvatarIceServersAsync');
        const iceServers = normalizeIceServers(iceResponse);

        session.peerConnection = new RTCPeerConnection({
            iceServers,
            iceTransportPolicy: 'relay'
        });

        wirePeerConnection(session);

        session.peerConnection.addTransceiver('video', { direction: 'sendrecv' });
        session.peerConnection.addTransceiver('audio', { direction: 'sendrecv' });

        await invoke(session, 'OnAvatarStatus', 'Creating WebRTC offer...');
        const offer = await session.peerConnection.createOffer();
        await session.peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(session.peerConnection);

        const localSdpBase64 = toBase64(JSON.stringify(session.peerConnection.localDescription));

        await invoke(session, 'OnAvatarStatus', 'Connecting avatar video...');
        const remoteSdpBase64 = await invoke(session, 'ConnectAvatarAsync', localSdpBase64);
        const remoteDescription = JSON.parse(fromBase64(remoteSdpBase64));
        await session.peerConnection.setRemoteDescription(new RTCSessionDescription(remoteDescription));

        await invoke(session, 'OnAvatarStarted');
        return session;
    } catch (error) {
        await invoke(session, 'OnAvatarError', getErrorMessage(error));
        await stopSession();
        throw error;
    }
}

export async function stopSession() {
    const session = currentSession;
    if (!session) {
        return;
    }

    currentSession = null;

    if (session.peerConnection) {
        closeQuietly(() => session.peerConnection.close());
    }

    if (session.videoElement) {
        session.videoElement.pause();
        session.videoElement.srcObject = null;
    }

    if (session.audioElement) {
        session.audioElement.pause();
        session.audioElement.srcObject = null;
    }

    await invoke(session, 'DisconnectAvatarAsync');
    await invoke(session, 'OnAvatarStopped', 'Stopped.');
}

function wirePeerConnection(session) {
    session.peerConnection.addEventListener('track', async (event) => {
        const stream = new MediaStream([event.track]);

        if (event.track.kind === 'video') {
            session.videoElement.srcObject = stream;
            session.videoElement.autoplay = true;
            session.videoElement.playsInline = true;
            await playMedia(session, session.videoElement, 'video');
        } else if (event.track.kind === 'audio') {
            session.audioElement.srcObject = stream;
            session.audioElement.autoplay = true;
            await playMedia(session, session.audioElement, 'audio');
        }
    });

    session.peerConnection.addEventListener('iceconnectionstatechange', async () => {
        const state = session.peerConnection.iceConnectionState;
        await invoke(session, 'OnAvatarStatus', `WebRTC status: ${state}.`);

        if ((state === 'failed' || state === 'disconnected' || state === 'closed') && currentSession === session) {
            await stopSession();
        }
    });
}

async function playMedia(session, element, label) {
    try {
        await element.play();
    } catch (error) {
        await invoke(session, 'OnAvatarError', `Browser blocked ${label} playback: ${getErrorMessage(error)}`);
    }
}

function normalizeIceServers(response) {
    if (!response?.succeeded && !response?.Succeeded) {
        throw new Error(response?.errorMessage || response?.ErrorMessage || 'Unable to fetch avatar relay token.');
    }

    const servers = response.iceServers || response.IceServers || [];
    if (!servers.length) {
        throw new Error('Azure did not return avatar relay ICE servers.');
    }

    return servers.map(server => ({
        urls: server.urls || server.Urls || [],
        username: server.username || server.Username || '',
        credential: server.credential || server.Credential || ''
    }));
}

function waitForIceGatheringComplete(peerConnection) {
    return new Promise((resolve) => {
        if (peerConnection.iceGatheringState === 'complete') {
            resolve();
            return;
        }

        let resolved = false;
        const timeout = window.setTimeout(() => {
            if (!resolved) {
                resolved = true;
                peerConnection.removeEventListener('icegatheringstatechange', onStateChange);
                resolve();
            }
        }, 10000);

        const onStateChange = () => {
            if (peerConnection.iceGatheringState === 'complete' && !resolved) {
                resolved = true;
                window.clearTimeout(timeout);
                peerConnection.removeEventListener('icegatheringstatechange', onStateChange);
                resolve();
            }
        };

        peerConnection.addEventListener('icegatheringstatechange', onStateChange);
    });
}

async function invoke(session, method, ...args) {
    if (!session?.dotNetReference) {
        return null;
    }

    return await session.dotNetReference.invokeMethodAsync(method, ...args);
}

function toBase64(value) {
    return btoa(unescape(encodeURIComponent(value)));
}

function fromBase64(value) {
    return decodeURIComponent(escape(atob(value)));
}

function getErrorMessage(error) {
    return error?.message || String(error);
}

function closeQuietly(action) {
    try {
        action();
    } catch {
        // Best effort cleanup.
    }
}
