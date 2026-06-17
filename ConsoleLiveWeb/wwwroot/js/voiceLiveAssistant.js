let currentSession = null;

export async function start(dotNetReference, audioElement) {
    if (currentSession) {
        await notifyStatus(currentSession, 'Voice Live is already running.');
        return;
    }

    const session = {
        dotNetReference,
        audioElement,
        dataChannel: null,
        peerConnection: null,
        remoteStream: new MediaStream(),
        signalingSocket: null,
        localStream: null,
        answerResolved: false,
        resolveAnswer: null,
        rejectAnswer: null
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

        await notifyStatus(session, 'Opening Voice Live signaling channel...');

        session.signalingSocket = new WebSocket(getSignalingUrl());
        wireSignalingSocket(session);
        await waitForSocketOpen(session.signalingSocket);

        session.peerConnection = new RTCPeerConnection();
        wirePeerConnection(session);

        session.dataChannel = session.peerConnection.createDataChannel('voice-live-events');
        wireDataChannel(session);

        for (const track of session.localStream.getTracks()) {
            session.peerConnection.addTrack(track, session.localStream);
        }

        const answerPromise = waitForAnswer(session);

        await notifyStatus(session, 'Negotiating WebRTC audio...');

        const offer = await session.peerConnection.createOffer();
        await session.peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(session.peerConnection);

        session.signalingSocket.send(JSON.stringify({
            type: 'rtc.call.sdp.create',
            sdp_offer: session.peerConnection.localDescription.sdp
        }));

        const answer = await answerPromise;
        await session.peerConnection.setRemoteDescription({
            type: 'answer',
            sdp: answer.sdp_answer
        });

        await invoke(session, 'OnVoiceLiveStarted');
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

    await stopSession(session, 'Stopped.');
}

async function stopSession(session, message) {
    currentSession = null;

    if (session.dataChannel) {
        closeQuietly(() => session.dataChannel.close());
    }

    if (session.peerConnection) {
        closeQuietly(() => session.peerConnection.close());
    }

    if (session.signalingSocket && session.signalingSocket.readyState <= WebSocket.OPEN) {
        closeQuietly(() => session.signalingSocket.close(1000, 'User stopped Voice Live.'));
    }

    if (session.localStream) {
        for (const track of session.localStream.getTracks()) {
            track.stop();
        }
    }

    if (session.audioElement) {
        session.audioElement.pause();
        session.audioElement.srcObject = null;
    }

    await invoke(session, 'OnVoiceLiveStopped', message);
}

function getSignalingUrl() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${window.location.host}/voice-live/signaling`;
}

function wireSignalingSocket(session) {
    session.signalingSocket.addEventListener('message', async (event) => {
        let message;

        try {
            message = JSON.parse(event.data);
        } catch {
            await invoke(session, 'OnVoiceLiveEvent', 'control.message', 'Received non-JSON control message.');
            return;
        }

        await publishEvent(session, message);

        if (message.type === 'rtc.call.sdp.created' && message.sdp_answer && session.resolveAnswer) {
            session.answerResolved = true;
            session.resolveAnswer(message);
            return;
        }

        if ((message.type === 'rtc.call.error' || message.type === 'error') && session.rejectAnswer && !session.answerResolved) {
            session.rejectAnswer(new Error(getMessageSummary(message) || 'Voice Live signaling failed.'));
        }
    });

    session.signalingSocket.addEventListener('close', async (event) => {
        if (currentSession === session) {
            await stopSession(session, event.reason || 'Signaling channel closed.');
        }

        if (session.rejectAnswer && !session.answerResolved) {
            session.rejectAnswer(new Error(event.reason || 'Signaling channel closed before SDP answer was received.'));
        }
    });

    session.signalingSocket.addEventListener('error', async () => {
        await notifyError(session, 'Voice Live signaling channel failed.');
    });
}

function wirePeerConnection(session) {
    session.audioElement.srcObject = session.remoteStream;
    session.audioElement.autoplay = true;

    session.peerConnection.addEventListener('track', async (event) => {
        session.remoteStream.addTrack(event.track);

        try {
            await session.audioElement.play();
        } catch (error) {
            await notifyError(session, `Browser blocked audio playback: ${getErrorMessage(error)}`);
        }
    });

    session.peerConnection.addEventListener('connectionstatechange', async () => {
        const state = session.peerConnection.connectionState;
        await notifyStatus(session, `WebRTC connection: ${state}.`);

        if ((state === 'failed' || state === 'disconnected' || state === 'closed') && currentSession === session) {
            await stopSession(session, `WebRTC connection ${state}.`);
        }
    });
}

function wireDataChannel(session) {
    session.dataChannel.addEventListener('open', async () => {
        await invoke(session, 'OnVoiceLiveEvent', 'data.open', 'Voice Live event channel opened.');
    });

    session.dataChannel.addEventListener('message', async (event) => {
        let message;

        try {
            message = JSON.parse(event.data);
        } catch {
            await invoke(session, 'OnVoiceLiveEvent', 'data.message', String(event.data));
            return;
        }

        await publishEvent(session, message);
    });

    session.dataChannel.addEventListener('close', async () => {
        await invoke(session, 'OnVoiceLiveEvent', 'data.closed', 'Voice Live event channel closed.');
    });
}

function waitForSocketOpen(socket) {
    return new Promise((resolve, reject) => {
        if (socket.readyState === WebSocket.OPEN) {
            resolve();
            return;
        }

        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', () => reject(new Error('Unable to open Voice Live signaling channel.')), { once: true });
    });
}

function waitForAnswer(session) {
    return new Promise((resolve, reject) => {
        const timeout = window.setTimeout(() => {
            reject(new Error('Timed out waiting for the Voice Live SDP answer.'));
        }, 45000);

        session.resolveAnswer = (message) => {
            window.clearTimeout(timeout);
            resolve(message);
        };

        session.rejectAnswer = (error) => {
            window.clearTimeout(timeout);
            reject(error);
        };
    });
}

function waitForIceGatheringComplete(peerConnection) {
    return new Promise((resolve) => {
        if (peerConnection.iceGatheringState === 'complete') {
            resolve();
            return;
        }

        const onStateChange = () => {
            if (peerConnection.iceGatheringState === 'complete') {
                peerConnection.removeEventListener('icegatheringstatechange', onStateChange);
                resolve();
            }
        };

        peerConnection.addEventListener('icegatheringstatechange', onStateChange);
    });
}

async function publishEvent(session, message) {
    const type = message.type || 'event';
    const summary = getMessageSummary(message);

    if (type === 'input_audio_buffer.speech_started') {
        await notifyStatus(session, 'User started speaking.');
    } else if (type === 'input_audio_buffer.speech_stopped') {
        await notifyStatus(session, 'Processing...');
    } else if (type === 'response.done') {
        await notifyStatus(session, 'Ready for next input.');
    } else if (type === 'session.updated') {
        await notifyStatus(session, 'Session configured. Start speaking.');
    }

    await invoke(session, 'OnVoiceLiveEvent', type, summary);
}

function getMessageSummary(message) {
    if (message.error?.message) {
        return message.error.message;
    }

    if (typeof message.delta === 'string') {
        return truncate(message.delta);
    }

    if (typeof message.transcript === 'string') {
        return truncate(message.transcript);
    }

    if (typeof message.text === 'string') {
        return truncate(message.text);
    }

    if (message.session?.id) {
        return message.session.id;
    }

    if (message.response?.status) {
        return message.response.status;
    }

    return '';
}

function truncate(value) {
    return value.length > 220 ? `${value.substring(0, 217)}...` : value;
}

async function notifyStatus(session, message) {
    await invoke(session, 'OnVoiceLiveStatus', message);
}

async function notifyError(session, message) {
    await invoke(session, 'OnVoiceLiveError', message);
}

async function invoke(session, method, ...args) {
    if (!session?.dotNetReference) {
        return;
    }

    try {
        await session.dotNetReference.invokeMethodAsync(method, ...args);
    } catch {
        // The Blazor circuit may already be disposed.
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
