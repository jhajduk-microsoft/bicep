// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
import vscode from "vscode";
import {
  createAzExtOutputChannel,
  registerUIExtensionVariables,
} from "@microsoft/vscode-azext-utils";

import {
  launchLanguageServiceWithProgressReport,
  BicepCacheContentProvider,
} from "./language";
import { BicepVisualizerViewManager } from "./visualizer";
import {
  BuildCommand,
  CommandManager,
  InsertResourceCommand,
  OpenBicepFileCommand,
  ShowSourceCommand,
  ShowVisualizerCommand,
  ShowVisualizerToSideCommand,
} from "./commands";
import {
  createLogger,
  resetLogger,
  activateWithTelemetryAndErrorHandling,
  Disposable,
} from "./utils";
import { CreateBicepFileCommand } from "./commands/gettingStarted/createBicepFile";

class BicepExtension extends Disposable {
  private constructor(public readonly extensionUri: vscode.Uri) {
    super();
  }

  public static create(context: vscode.ExtensionContext) {
    const extension = new BicepExtension(context.extensionUri);
    context.subscriptions.push(extension);

    return extension;
  }
}

export async function activate(
  context: vscode.ExtensionContext
): Promise<void> {
  const extension = BicepExtension.create(context);
  const outputChannel = createAzExtOutputChannel("Bicep", "bicep");

  extension.register(outputChannel);
  extension.register(createLogger(context, outputChannel));
  registerUIExtensionVariables({ context, outputChannel });

  await activateWithTelemetryAndErrorHandling(async (actionContext) => {
    const languageClient = await launchLanguageServiceWithProgressReport(
      actionContext,
      context,
      outputChannel
    );

    // go2def links that point to the bicep cache will have the bicep-cache scheme in their document URIs
    // this content provider will allow VS code to understand that scheme
    // and surface the content as a read-only file
    extension.register(
      vscode.workspace.registerTextDocumentContentProvider(
        "bicep-cache",
        new BicepCacheContentProvider(languageClient)
      )
    );

    const viewManager = extension.register(
      new BicepVisualizerViewManager(extension.extensionUri, languageClient)
    );

    // Register commands.
    await extension
      .register(new CommandManager(context))
      .registerCommands(
        new BuildCommand(languageClient),
        new InsertResourceCommand(languageClient),
        new ShowVisualizerCommand(viewManager),
        new ShowVisualizerToSideCommand(viewManager),
        new ShowSourceCommand(viewManager),
        new CreateBicepFileCommand(),
        new OpenBicepFileCommand()
      );
  });
}

export function deactivate(): void {
  resetLogger();
}
