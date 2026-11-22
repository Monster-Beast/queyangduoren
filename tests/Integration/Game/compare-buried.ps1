param(
    [Parameter(Mandatory)][string]$HostPng,
    [Parameter(Mandatory)][string]$GuestPng
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
function Get-CellImage([string]$Path) {
    $image=[Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Path))
    $tile=[Drawing.Bitmap]::new(64,64)
    $g=[Drawing.Graphics]::FromImage($tile)
    try {
        # Orthographic size 5 gives ten visible cells vertically. Both cameras
        # independently verify and hold this cell at (0.5,0.5) before saving.
        $size=[int][Math]::Round($image.Height/10)
        $src=[Drawing.Rectangle]::new([int](($image.Width-$size)/2),[int](($image.Height-$size)/2),$size,$size)
        $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        $g.DrawImage($image,[Drawing.Rectangle]::new(0,0,64,64),$src,[Drawing.GraphicsUnit]::Pixel)
    } finally { $g.Dispose();$image.Dispose() }
    return $tile
}
$a=Get-CellImage $HostPng
$b=Get-CellImage $GuestPng
try {
    $pixels=0; $wrong=0; $sum=0
    for($y=0;$y -lt 64;$y++) {
        for($x=0;$x -lt 64;$x++) {
            $left=$a.GetPixel($x,$y);$right=$b.GetPixel($x,$y)
            $dr=[int]$left.R-$right.R;$dg=[int]$left.G-$right.G;$db=[int]$left.B-$right.B
            $sum+=[Math]::Abs($dr)+[Math]::Abs($dg)+[Math]::Abs($db)
            # Compare the visible material colour independently of a common light offset.
            # Occluding a white mineral with brown metal changes channel differences.
            if([Math]::Max($left.R,$right.R) -lt 48) { continue }
            $pixels++
            if([Math]::Max([Math]::Abs($dr-$dg),[Math]::Abs($db-$dr)) -gt 24) { $wrong++ }
        }
    }
    $fraction=if ($pixels) { $wrong/[double]$pixels } else { 0 }
    $mae=$sum/(4096*3.0)
    Write-Output ('Buried image comparison RGB MAE={0:F2}/255 material mismatch={1:P2} ({2}/{3})' -f $mae,$fraction,$wrong,$pixels)
    if($mae -gt 6 -or $fraction -gt .015) { throw "Excavation centre pixels show different art or material occlusion." }
} finally { $a.Dispose();$b.Dispose() }
