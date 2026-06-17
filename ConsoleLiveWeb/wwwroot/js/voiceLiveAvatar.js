let currentSession = null;

export async function start(dotNetReference, videoElement, audioElement, sessionPath = '/voice-live-avatar/session') {
    if (currentSession) {
        await notifyStatus(currentSession, 'Voice Live Avatar is already running.');
        return;
    }

    const session = {
        dotNetReference,
        videoElement,
        audioElement,
        audioContext: null,
        audioProcessor: null,
        audioSource: null,
        avatarConnected: false,
        avatarConnecting: false,
        localStream: null,
        peerConnection: null,
        websocket: null,
        sessionPath
    };

    currentSession = session;

    try {
        await notifyStatus(session, 'Requesting microphone permission...');
        session.localStream = await navigator.mediaDevices.getUserMedia({
            audio: {
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true
            }
        });

        await notifyStatus(session, 'Opening Voice Live Avatar session...');
        session.websocket = new WebSocket(getSessionUrl(session.sessionPath));
        wireWebSocket(session);
        await waitForSocketOpen(session.websocket);
    } catch (error) {
        await notifyError(session, getErrorMessage(error));
        await stop();
        throw error;
    }
}

export async function stop() {
    const session = currentSession;
    if (!session) {
        return;
    }

    currentSession = null;
    cleanupSession(session);
    await invoke(session, 'OnVoiceLiveAvatarStopped', 'Stopped.');
}

function cleanupSession(session) {
    stopAudioProcessing(session);

    if (session.localStream) {
        for (const track of session.localStream.getTracks()) {
            track.stop();
        }
    }

    if (session.peerConnection) {
        closeQuietly(() => session.peerConnection.close());
    }

    if (session.websocket && session.websocket.readyState <= WebSocket.OPEN) {
        closeQuietly(() => session.websocket.close(1000, 'User stopped Voice Live Avatar.'));
    }

    if (session.videoElement) {
        session.videoElement.pause();
        session.videoElement.srcObject = null;
    }

    if (session.audioElement) {
        session.audioElement.pause();
        session.audioElement.srcObject = null;
    }
}

function getSessionUrl(sessionPath) {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${window.location.host}${sessionPath}`;
}

function wireWebSocket(session) {
    session.websocket.addEventListener('message', async (event) => {
        let message;

        try {
            message = JSON.parse(event.data);
        } catch {
            await publishEvent(session, 'server.message', 'Received non-JSON Voice Live message.');
            return;
        }

        await handleServerMessage(session, message);
    });

    session.websocket.addEventListener('close', async (event) => {
        if (currentSession === session) {
            currentSession = null;
            cleanupSession(session);
            await invoke(session, 'OnVoiceLiveAvatarStopped', event.reason || 'Voice Live Avatar session closed.');
        }
    });

    session.websocket.addEventListener('error', async () => {
        await notifyError(session, 'Voice Live Avatar WebSocket failed.');
    });
}

async function handleServerMessage(session, message) {
    const type = message.type || 'event';
    const summary = getMessageSummary(message);

    switch (type) {
        case 'proxy.connected':
        case 'proxy.status':
            await notifyStatus(session, summary || message.message || type);
            break;
        case 'tool.session.update.sent':
            await notifyStatus(session, 'WebIQ function configured for avatar session.');
            break;
        case 'tool.requested':
            await notifyStatus(session, summary || 'Voice Live requested WebIQ.');
            break;
        case 'tool.arguments':
            await notifyStatus(session, summary ? `WebIQ query: ${summary}` : 'WebIQ query received.');
            break;
        case 'tool.webiq.started':
            await notifyStatus(session, summary || 'Looking up WebIQ.');
            break;
        case 'tool.webiq.completed':
            await notifyStatus(session, 'WebIQ returned real-time context.');
            break;
        case 'tool.webiq.failed':
            await notifyStatus(session, summary || 'WebIQ lookup failed.');
            break;
        case 'tool.output.sent':
        case 'tool.response.created':
            await notifyStatus(session, summary || type);
            break;
        case 'proxy.error':
        case 'error':
            await notifyError(session, summary || message.message || 'Voice Live Avatar error.');
            break;
        case 'session.created':
            await notifyStatus(session, 'Voice Live session created.');
            break;
        case 'session.updated':
            await notifyStatus(session, 'Session configured. Connecting avatar video...');
            await connectAvatarIfNeeded(session, message.session?.avatar?.ice_servers);
            break;
        case 'session.avatar.connecting':
            await applyAvatarAnswer(session, message.server_sdp);
            break;
        case 'input_audio_buffer.speech_started':
            await notifyStatus(session, 'User started speaking.');
            break;
        case 'input_audio_buffer.speech_stopped':
            await notifyStatus(session, 'Processing...');
            break;
        case 'response.created':
            await notifyStatus(session, 'Assistant responding...');
            break;
        case 'response.done':
            await notifyStatus(session, 'Ready for next input.');
            break;
        case 'conversation.item.input_audio_transcription.completed':
            await notifyStatus(session, 'User transcript received.');
            break;
        case 'warning':
            await notifyStatus(session, summary || 'Voice Live warning.');
            break;
    }

    await publishEvent(session, type, summary);
}

async function connectAvatarIfNeeded(session, iceServers) {
    if (session.avatarConnected || session.avatarConnecting) {
        return;
    }

    if (!Array.isArray(iceServers) || iceServers.length === 0) {
        await notifyError(session, 'Azure did not return avatar ICE servers. Confirm the resource supports Voice Live avatar.');
        return;
    }

    session.avatarConnecting = true;
    session.peerConnection = new RTCPeerConnection({
        iceServers: iceServers.map(server => ({
            urls: server.urls || [],
            username: server.username || '',
            credential: server.credential || ''
        })),
        iceTransportPolicy: 'relay'
    });

    wirePeerConnection(session);
    session.peerConnection.addTransceiver('video', { direction: 'sendrecv' });
    session.peerConnection.addTransceiver('audio', { direction: 'sendrecv' });

    const offer = await session.peerConnection.createOffer();
    await session.peerConnection.setLocalDescription(offer);
    await waitForIceGatheringComplete(session.peerConnection);

    sendVoiceLiveEvent(session, {
        type: 'session.avatar.connect',
        client_sdp: toBase64(JSON.stringify(session.peerConnection.localDescription))
    });
}

function wirePeerConnection(session) {
    session.peerConnection.addEventListener('track', async (event) => {
        const stream = event.streams?.[0] || new MediaStream([event.track]);

        if (event.track.kind === 'video') {
            session.videoElement.srcObject = stream;
            session.videoElement.autoplay = true;
            session.videoElement.playsInline = true;
            await playMedia(session, session.videoElement, 'video');
            return;
        }

        if (event.track.kind === 'audio') {
            session.audioElement.srcObject = stream;
            session.audioElement.autoplay = true;
            await playMedia(session, session.audioElement, 'audio');
        }
    });

    session.peerConnection.addEventListener('iceconnectionstatechange', async () => {
        const state = session.peerConnection.iceConnectionState;
        await notifyStatus(session, `Avatar WebRTC status: ${state}.`);

        if ((state === 'failed' || state === 'disconnected' || state === 'closed') && currentSession === session) {
            await publishEvent(session, 'avatar.webrtc', `Avatar WebRTC ${state}.`);
        }
    });
}

async function applyAvatarAnswer(session, serverSdp) {
    if (!session.peerConnection || !serverSdp) {
        await notifyError(session, 'Azure did not return an avatar SDP answer.');
        return;
    }

    const remoteDescription = decodeSessionDescription(serverSdp);
    await session.peerConnection.setRemoteDescription(new RTCSessionDescription(remoteDescription));
    session.avatarConnected = true;
    session.avatarConnecting = false;

    await startAudioProcessing(session);
    await invoke(session, 'OnVoiceLiveAvatarStarted');
}

async function startAudioProcessing(session) {
    if (session.audioProcessor) {
        return;
    }

    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
    session.audioContext = new AudioContextCtor();
    session.audioSource = session.audioContext.createMediaStreamSource(session.localStream);
    session.audioProcessor = session.audioContext.createScriptProcessor(4096, 1, 1);

    session.audioProcessor.onaudioprocess = (event) => {
        if (currentSession !== session || session.websocket?.readyState !== WebSocket.OPEN) {
            return;
        }

        const input = event.inputBuffer.getChannelData(0);
        const output = event.outputBuffer.getChannelData(0);
        output.fill(0);

        const pcm16 = floatToPcm16(resample(input, session.audioContext.sampleRate, 24000));
        if (pcm16.byteLength === 0) {
            return;
        }

        sendVoiceLiveEvent(session, {
            type: 'input_audio_buffer.append',
            audio: arrayBufferToBase64(pcm16.buffer)
        });
    };

    session.audioSource.connect(session.audioProcessor);
    session.audioProcessor.connect(session.audioContext.destination);
    await notifyStatus(session, 'Connected. Start speaking.');
}

function stopAudioProcessing(session) {
    closeQuietly(() => session.audioProcessor?.disconnect());
    closeQuietly(() => session.audioSource?.disconnect());
    closeQuietly(() => session.audioContext?.close());

    session.audioProcessor = null;
    session.audioSource = null;
    session.audioContext = null;
}

function sendVoiceLiveEvent(session, payload) {
    if (session.websocket?.readyState === WebSocket.OPEN) {
        session.websocket.send(JSON.stringify(payload));
    }
}

function waitForSocketOpen(socket) {
    return new Promise((resolve, reject) => {
        if (socket.readyState === WebSocket.OPEN) {
            resolve();
            return;
        }

        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', () => reject(new Error('Unable to open Voice Live Avatar session.')), { once: true });
    });
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

function resample(input, sourceRate, targetRate) {
    if (sourceRate === targetRate) {
        return input;
    }

    const ratio = sourceRate / targetRate;
    const outputLength = Math.floor(input.length / ratio);
    const output = new Float32Array(outputLength);

    for (let i = 0; i < outputLength; i++) {
        const position = i * ratio;
        const index = Math.floor(position);
        const fraction = position - index;
        const sample = input[index] * (1 - fraction) + (input[index + 1] || input[index]) * fraction;
        output[i] = sample;
    }

    return output;
}

function floatToPcm16(input) {
    const output = new Int16Array(input.length);

    for (let i = 0; i < input.length; i++) {
        const sample = Math.max(-1, Math.min(1, input[i]));
        output[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
    }

    return output;
}

function arrayBufferToBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    const chunkSize = 0x8000;
    let binary = '';

    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
    }

    return btoa(binary);
}

function toBase64(value) {
    return btoa(unescape(encodeURIComponent(value)));
}

function fromBase64(value) {
    return decodeURIComponent(escape(atob(value)));
}

function decodeSessionDescription(value) {
    if (value.startsWith('v=0')) {
        return {
            type: 'answer',
            sdp: value
        };
    }

    const decoded = fromBase64(value);

    if (decoded.startsWith('v=0')) {
        return {
            type: 'answer',
            sdp: decoded
        };
    }

    return JSON.parse(decoded);
}

function getMessageSummary(message) {
    if (message.message) {
        return message.message;
    }

    if (message.error?.message) {
        return message.error.message;
    }

    if (message.warning?.message) {
        return message.warning.message;
    }

    if (typeof message.delta === 'string' && message.delta !== '[delta omitted]') {
        return truncate(message.delta);
    }

    const details = [];
    const item = message.item || {};

    appendDetail(details, 'name', message.name || item.name);
    appendDetail(details, 'call_id', message.call_id || item.call_id);

    if (typeof message.arguments === 'string') {
        appendDetail(details, 'arguments', message.arguments);
    }

    if (typeof message.transcript === 'string') {
        appendDetail(details, 'transcript', message.transcript);
    }

    if (message.item?.content?.length) {
        const transcripts = message.item.content
            .map(part => part?.transcript || part?.text)
            .filter(Boolean);

        if (transcripts.length) {
            appendDetail(details, 'content', transcripts.join(' '));
        }
    }

    appendDetail(details, 'status', message.response?.status);
    appendDetail(details, 'session', message.session?.id);

    if (details.length > 0) {
        return truncate(details.join(' | '));
    }

    return '';
}

function appendDetail(details, label, value) {
    if (value === null || value === undefined) {
        return;
    }

    const text = typeof value === 'string' ? value : String(value);
    if (!text.trim()) {
        return;
    }

    details.push(`${label}=${truncate(text.trim())}`);
}

function truncate(value) {
    return value.length > 260 ? `${value.substring(0, 257)}...` : value;
}

async function publishEvent(session, type, message) {
    await invoke(session, 'OnVoiceLiveAvatarEvent', type, message || '');
}

async function notifyStatus(session, message) {
    await invoke(session, 'OnVoiceLiveAvatarStatus', message);
}

async function notifyError(session, message) {
    await invoke(session, 'OnVoiceLiveAvatarError', message);
}

async function playMedia(session, element, label) {
    try {
        await element.play();
    } catch (error) {
        await notifyError(session, `Browser blocked ${label} playback: ${getErrorMessage(error)}`);
    }
}

async function invoke(session, method, ...args) {
    if (!session?.dotNetReference) {
        return null;
    }

    try {
        return await session.dotNetReference.invokeMethodAsync(method, ...args);
    } catch {
        return null;
    }
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
