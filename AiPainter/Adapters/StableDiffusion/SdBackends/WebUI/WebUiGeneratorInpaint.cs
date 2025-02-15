﻿using AiPainter.Helpers;
using AiPainter.Adapters.StableDiffusion.SdBackends.WebUI.SdApiClientStuff;
using System.Diagnostics;

namespace AiPainter.Adapters.StableDiffusion.SdBackends.WebUI;

class WebUiGeneratorInpaint : ISdGenerator
{
    private readonly SdListItemGeneration control;

    private readonly Bitmap originalImage;
    private readonly Rectangle activeBox;
    private readonly Bitmap? croppedMask;
    private readonly string originalFilePath;

    public WebUiGeneratorInpaint(SdListItemGeneration control, Bitmap originalImage, Rectangle activeBox, Bitmap? croppedMask, string originalFilePath)
    {
        this.control = control;

        this.activeBox = activeBox;
        this.originalImage = originalImage;
        this.croppedMask = croppedMask;
        this.originalFilePath = originalFilePath;
    }

    public async Task<bool> RunAsync(SdGenerationParameters sdGenerationParameters)
    {
        Debug.Assert(sdGenerationParameters.seed > 0);

        var wasProgressShown = false;
        var isCheckpointSuccess = await WebUiGeneratorHelper.PrepareCheckpointAsync
        (
            false,
            sdGenerationParameters.checkpointName,
            sdGenerationParameters.vaeName,
            () => control.IsDisposed,
            s => { wasProgressShown = true; control.NotifyStepsCustomText(s); }
        );
        if (!isCheckpointSuccess) return false;
        if (!wasProgressShown) control.NotifyProgress(0);

        using var croppedImage = BitmapTools.GetCropped(originalImage, activeBox, Color.Black);

        var parameters = new SdImg2ImgRequest
        {
            prompt = sdGenerationParameters.prompt,
            negative_prompt = sdGenerationParameters.negative,
            cfg_scale = sdGenerationParameters.cfgScale,
            steps = sdGenerationParameters.steps,

            seed = sdGenerationParameters.seed,
            subseed_strength = sdGenerationParameters.seedVariationStrength,

            init_images = new[] { BitmapTools.ToDataUri(croppedImage) },
            mask = croppedMask != null ? BitmapTools.ToDataUri(croppedMask) : null,

            width = croppedImage.Width,
            height = croppedImage.Height,

            sampler_index = sdGenerationParameters.sampler,

            // looks like webui use 'fill' as default if mask specified, so force to use 'original'
            inpainting_fill = sdGenerationParameters.inpaintingFill ?? SdInpaintingFill.original,
            denoising_strength = sdGenerationParameters.changesLevel,

            override_settings = new SdSettings
            {
                CLIP_stop_at_last_layers = sdGenerationParameters.clipSkip
            }
        };
        var response = await WebUiApiClient.img2imgAsync(parameters, onProgress: step => control.NotifyProgress(step));

        if (response == null)
        {
            control.NotifyProgress(sdGenerationParameters.steps);
            throw new SdGeneratorFatalErrorException("ERROR");
        }

        // SD not ready, need retry later
        if (response.images == null) throw new SdListItemGenerationNeedRetryException();

        if (control.IsWantToCancelProcessingResultOfCurrentGeneration)
        {
            control.NotifyProgress(sdGenerationParameters.steps);
            return false;
        }

        processGenerationResult(sdGenerationParameters, BitmapTools.FromDataUri(response.images[0]));

        control.NotifyProgress(sdGenerationParameters.steps);

        return true;
    }

    public void Cancel()
    {
        Task.Run(WebUiApiClient.Cancel);
    }
    
    private void processGenerationResult(SdGenerationParameters sdGenerationParameters, Bitmap resultImage)
    {
        try
        {
            using var tempOriginalImage = BitmapTools.Clone(originalImage);
            BitmapTools.DrawBitmapAtPos(resultImage, tempOriginalImage, activeBox.X, activeBox.Y);
            resultImage.Dispose();
            SdGeneratorHelper.SaveInpaint(sdGenerationParameters, originalFilePath, tempOriginalImage);
        }
        catch (Exception ee)
        {
            WebUiApiClient.Log.WriteLine(ee.ToString());
        }
    }
}
