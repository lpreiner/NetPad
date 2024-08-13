import {ipcRenderer} from "electron";
import {IHttpClient} from "@aurelia/fetch-client";
import {ChannelInfo, IWindowService, IWindowState, WindowApiClient, WindowState, WindowViewStatus} from "@application";
import {ElectronIpcEventNames} from "../electron-ipc-event-names";
import {ElectronIpcGateway} from "./electron-ipc-gateway";

/**
 * IWindowService implementation that sends window control events to Electron's main process.
 */
export class ElectronWindowService extends WindowApiClient implements IWindowService {
    constructor(private readonly electronIpcGateway: ElectronIpcGateway, baseUrl?: string, @IHttpClient http?: IHttpClient) {
        super(baseUrl, http);
    }

    public async getState(): Promise<WindowState> {
        const state = await ipcRenderer.invoke(ElectronIpcEventNames.getWindowState) as IWindowState;

        if (!state) {
            return new WindowState(WindowViewStatus.Unknown, false);
        }

        return new WindowState(state.viewStatus, state.isAlwaysOnTop);
    }

    maximize(): Promise<void> {
        return this.electronIpcGateway.send(new ChannelInfo(ElectronIpcEventNames.maximize));
    }

    minimize(): Promise<void> {
        return this.electronIpcGateway.send(new ChannelInfo(ElectronIpcEventNames.minimize));
    }

    toggleDeveloperTools(): Promise<void> {
        return this.electronIpcGateway.send(new ChannelInfo(ElectronIpcEventNames.toggleDeveloperTools));
    }

    toggleAlwaysOnTop(): Promise<void> {
        return this.electronIpcGateway.send(new ChannelInfo(ElectronIpcEventNames.toggleAlwaysOnTop));
    }

    toggleFullScreen(): Promise<void> {
        return this.electronIpcGateway.send(new ChannelInfo(ElectronIpcEventNames.toggleFullScreen));
    }
}
