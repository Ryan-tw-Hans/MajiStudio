using System;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Pure mathematical helper for coordinate transformations between viewport/visual coordinates
/// and original image pixel coordinates under zoom, pan, letterboxing/pillarboxing, and viewport resizing.
/// Conforms to RS-ISSUE-0008-v0.1, AR-ISSUE-0008-v0.1, and DP-ISSUE-0008-v0.1.
/// </summary>
public static class ImageTransformHelper
{
    /// <summary>
    /// Calculates the uniform scale factor required to fit the entire image proportionally inside the viewport.
    /// Returns 1.0 if image or viewport dimensions are invalid.
    /// </summary>
    public static double CalculateFitScale(double viewportWidth, double viewportHeight, int imageWidth, int imageHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return 1.0;
        }

        double scaleX = viewportWidth / imageWidth;
        double scaleY = viewportHeight / imageHeight;
        return Math.Min(scaleX, scaleY);
    }

    /// <summary>
    /// Computes the rendered image rectangle within the viewport coordinate system.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) GetRenderedImageRect(
        double viewportWidth,
        double viewportHeight,
        int imageWidth,
        int imageHeight,
        double scale,
        double panX,
        double panY)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || scale <= 0)
        {
            return (0, 0, 0, 0);
        }

        double renderedW = imageWidth * scale;
        double renderedH = imageHeight * scale;

        // Centered inside viewport plus pan offsets
        double renderedLeft = ((viewportWidth - renderedW) / 2.0) + panX;
        double renderedTop = ((viewportHeight - renderedH) / 2.0) + panY;

        return (renderedLeft, renderedTop, renderedW, renderedH);
    }

    /// <summary>
    /// Converts a visual selection rectangle (in viewport coordinates) into original image pixel coordinates.
    /// Accurately intersects with the rendered image area, discards letterboxing/pillarboxing,
    /// clamps to image boundaries [0, ImageWidth] and [0, ImageHeight], and validates minimal size.
    /// </summary>
    /// <returns>True if a valid intersection of at least 1x1 image pixels exists; false otherwise.</returns>
    public static bool VisualRoiToImagePixelRoi(
        double visualX,
        double visualY,
        double visualWidth,
        double visualHeight,
        double viewportWidth,
        double viewportHeight,
        int imageWidth,
        int imageHeight,
        double scale,
        double panX,
        double panY,
        out int origX,
        out int origY,
        out int origWidth,
        out int origHeight)
    {
        origX = 0;
        origY = 0;
        origWidth = 0;
        origHeight = 0;

        if (imageWidth <= 0 || imageHeight <= 0 || scale <= 0 || visualWidth <= 0 || visualHeight <= 0)
        {
            return false;
        }

        var (imgLeft, imgTop, imgW, imgH) = GetRenderedImageRect(
            viewportWidth, viewportHeight, imageWidth, imageHeight, scale, panX, panY);

        // Calculate visual selection bounding box
        double selLeft = visualX;
        double selTop = visualY;
        double selRight = visualX + visualWidth;
        double selBottom = visualY + visualHeight;

        // Intersection of selection rectangle with rendered image area
        double intersectLeft = Math.Max(selLeft, imgLeft);
        double intersectTop = Math.Max(selTop, imgTop);
        double intersectRight = Math.Min(selRight, imgLeft + imgW);
        double intersectBottom = Math.Min(selBottom, imgTop + imgH);

        // If no intersection with the image area (selection completely in letterbox or outside)
        if (intersectRight <= intersectLeft || intersectBottom <= intersectTop)
        {
            return false;
        }

        // Map intersection rectangle to original image pixel coordinates
        double pxLeft = (intersectLeft - imgLeft) / scale;
        double pxTop = (intersectTop - imgTop) / scale;
        double pxRight = (intersectRight - imgLeft) / scale;
        double pxBottom = (intersectBottom - imgTop) / scale;

        // Clamp to image bounds [0, imageWidth] and [0, imageHeight]
        int clampedX = Math.Clamp((int)Math.Round(pxLeft), 0, imageWidth);
        int clampedY = Math.Clamp((int)Math.Round(pxTop), 0, imageHeight);
        int clampedRight = Math.Clamp((int)Math.Round(pxRight), 0, imageWidth);
        int clampedBottom = Math.Clamp((int)Math.Round(pxBottom), 0, imageHeight);

        int w = Math.Max(0, clampedRight - clampedX);
        int h = Math.Max(0, clampedBottom - clampedY);

        if (w <= 0 || h <= 0)
        {
            return false;
        }

        origX = clampedX;
        origY = clampedY;
        origWidth = w;
        origHeight = h;
        return true;
    }

    /// <summary>
    /// Converts an original image pixel ROI into visual viewport coordinates for display.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) ImagePixelRoiToVisualRoi(
        int origX,
        int origY,
        int origWidth,
        int origHeight,
        double viewportWidth,
        double viewportHeight,
        int imageWidth,
        int imageHeight,
        double scale,
        double panX,
        double panY)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || scale <= 0 || origWidth <= 0 || origHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        var (imgLeft, imgTop, _, _) = GetRenderedImageRect(
            viewportWidth, viewportHeight, imageWidth, imageHeight, scale, panX, panY);

        double visualX = imgLeft + (origX * scale);
        double visualY = imgTop + (origY * scale);
        double visualW = origWidth * scale;
        double visualH = origHeight * scale;

        return (visualX, visualY, visualW, visualH);
    }

    /// <summary>
    /// Clamps pan offsets to ensure the image is never panned completely out of view.
    /// Retains at least minVisibleMargin pixels of the image inside the viewport.
    /// </summary>
    public static (double ClampedPanX, double ClampedPanY) ClampPan(
        double panX,
        double panY,
        double viewportWidth,
        double viewportHeight,
        int imageWidth,
        int imageHeight,
        double scale,
        double minVisibleMargin = 40.0)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0 || scale <= 0)
        {
            return (0, 0);
        }

        double renderedW = imageWidth * scale;
        double renderedH = imageHeight * scale;

        // Image left boundary in viewport: ((viewportWidth - renderedW) / 2.0) + panX
        // We want ImageRight >= minVisibleMargin AND ImageLeft <= viewportWidth - minVisibleMargin
        // Base centered position:
        double baseLeft = (viewportWidth - renderedW) / 2.0;
        double baseTop = (viewportHeight - renderedH) / 2.0;

        // panX constraints:
        // baseLeft + panX + renderedW >= minVisibleMargin => panX >= minVisibleMargin - baseLeft - renderedW
        // baseLeft + panX <= viewportWidth - minVisibleMargin => panX <= viewportWidth - minVisibleMargin - baseLeft
        double minPanX = minVisibleMargin - baseLeft - renderedW;
        double maxPanX = viewportWidth - minVisibleMargin - baseLeft;

        // panY constraints:
        double minPanY = minVisibleMargin - baseTop - renderedH;
        double maxPanY = viewportHeight - minVisibleMargin - baseTop;

        if (minPanX > maxPanX)
        {
            // If viewport is smaller than 2 * minVisibleMargin, center pan
            panX = 0;
        }
        else
        {
            panX = Math.Clamp(panX, minPanX, maxPanX);
        }

        if (minPanY > maxPanY)
        {
            panY = 0;
        }
        else
        {
            panY = Math.Clamp(panY, minPanY, maxPanY);
        }

        return (panX, panY);
    }

    /// <summary>
    /// Validates whether a pointer pressed event is eligible to begin a workspace gesture (pan or ROI).
    /// Strictly requires that an image session is loaded and that the primary/left button was pressed.
    /// Right-click or middle-click returns false.
    /// </summary>
    public static bool ShouldAcceptPointerPress(bool isLeftButtonPressed, bool hasImage)
    {
        return hasImage && isLeftButtonPressed;
    }

    /// <summary>
    /// Computes new zoom scale and pan offsets when zooming with a visual anchor point (e.g. mouse pointer or viewport center).
    /// Conforms to RS-ISSUE-0009 OCR9-01, OCR9-02 and AR-ISSUE-0009.
    /// If the pointer is inside the rendered image rectangle, that exact image pixel remains under the pointer after zoom.
    /// If the pointer is in the letterbox/margin/whitespace, the visible image center inside the viewport acts as the anchor.
    /// Clamps scale within [minScale, maxScale] and clamps pan offsets via ClampPan so the image never disappears from view.
    /// </summary>
    public static (double NewScale, double NewPanX, double NewPanY) CalculateZoomAtPoint(
        double viewportWidth,
        double viewportHeight,
        int imageWidth,
        int imageHeight,
        double currentScale,
        double currentPanX,
        double currentPanY,
        double pointerX,
        double pointerY,
        double zoomMultiplier,
        double minScale = 0.1,
        double maxScale = 5.0,
        double minVisibleMargin = 40.0)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0 || currentScale <= 0)
        {
            return (currentScale, currentPanX, currentPanY);
        }

        double targetScale = Math.Clamp(currentScale * zoomMultiplier, minScale, maxScale);
        if (Math.Abs(targetScale - currentScale) < 1e-6)
        {
            var (clampedSamePanX, clampedSamePanY) = ClampPan(
                currentPanX, currentPanY, viewportWidth, viewportHeight, imageWidth, imageHeight, targetScale, minVisibleMargin);
            return (targetScale, clampedSamePanX, clampedSamePanY);
        }

        var (imgLeft, imgTop, imgW, imgH) = GetRenderedImageRect(
            viewportWidth, viewportHeight, imageWidth, imageHeight, currentScale, currentPanX, currentPanY);

        double anchorX;
        double anchorY;

        bool isInsideImage = pointerX >= imgLeft && pointerX <= imgLeft + imgW &&
                             pointerY >= imgTop && pointerY <= imgTop + imgH;

        if (isInsideImage)
        {
            anchorX = pointerX;
            anchorY = pointerY;
        }
        else
        {
            // Indicator in margin / letterbox / whitespace: use visible image center inside viewport
            double visLeft = Math.Max(0, imgLeft);
            double visRight = Math.Min(viewportWidth, imgLeft + imgW);
            double visTop = Math.Max(0, imgTop);
            double visBottom = Math.Min(viewportHeight, imgTop + imgH);

            if (visRight > visLeft && visBottom > visTop)
            {
                anchorX = (visLeft + visRight) / 2.0;
                anchorY = (visTop + visBottom) / 2.0;
            }
            else
            {
                anchorX = imgLeft + (imgW / 2.0);
                anchorY = imgTop + (imgH / 2.0);
            }
        }

        double ratio = targetScale / currentScale;
        double newPanX = currentPanX + (1.0 - ratio) * (anchorX - (viewportWidth / 2.0) - currentPanX);
        double newPanY = currentPanY + (1.0 - ratio) * (anchorY - (viewportHeight / 2.0) - currentPanY);

        var (clampedPanX, clampedPanY) = ClampPan(
            newPanX, newPanY, viewportWidth, viewportHeight, imageWidth, imageHeight, targetScale, minVisibleMargin);

        return (targetScale, clampedPanX, clampedPanY);
    }
}
