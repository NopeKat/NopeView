#!/usr/bin/env pwsh
# Builds the native libraries the Windows app loads: the codec router, the file
# list service and one dynamic library per image format. Output goes to
# build/windows/native/x64; -Install copies it to src/windows/Native/x64, which
# is where the project keeps the binaries it ships.

param(
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'build-common.ps1')

$workspace = Get-WorkspaceRoot -ScriptRoot $PSScriptRoot
$repo = Join-Path $workspace '.repos\NopeView'
if (-not (Test-Path -LiteralPath $repo)) {
    throw "Repository not found: $repo"
}

function Resolve-Msvc {
    $installations = @()

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $latest = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($latest) { $installations += $latest.Trim() }
    }

    # Visual Studio installs live two levels below the product root, as
    # <year>\<edition>; the enumerations that follow cover the ones vswhere
    # does not report.
    foreach ($root in 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($year in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            foreach ($edition in Get-ChildItem -LiteralPath $year.FullName -Directory -ErrorAction SilentlyContinue) {
                $installations += $edition.FullName
            }
        }
    }

    $candidates = @()
    foreach ($installation in $installations) {
        $tools = Join-Path $installation 'VC\Tools\MSVC'
        if (-not (Test-Path -LiteralPath $tools)) { continue }
        foreach ($toolset in Get-ChildItem -LiteralPath $tools -Directory -ErrorAction SilentlyContinue) {
            $cl = Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'
            if (Test-Path -LiteralPath $cl) { $candidates += $toolset.FullName }
        }
    }

    $selected = $candidates | Sort-Object -Descending | Select-Object -First 1
    if (-not $selected) {
        throw 'No MSVC x64 compiler found. Install the Visual C++ build tools.'
    }
    return $selected
}

function Resolve-WindowsSdk {
    $root = 'C:\Program Files (x86)\Windows Kits\10'
    $versions = Get-ChildItem -LiteralPath (Join-Path $root 'Include') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object Name -Descending
    foreach ($version in $versions) {
        $lib = Join-Path $root "Lib\$($version.Name)\ucrt\x64"
        if (Test-Path -LiteralPath $lib) { return $version.Name }
    }
    throw 'No Windows 10 SDK with x64 libraries found.'
}

$msvc = Resolve-Msvc
$sdkVersion = Resolve-WindowsSdk
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$cl = Join-Path $msvc 'bin\Hostx64\x64\cl.exe'

# cl reads its search paths from the environment, so the script sets the ones
# the toolset and the SDK would provide through vcvars.
$env:INCLUDE = @(
    (Join-Path $msvc 'include'),
    (Join-Path $sdkRoot "Include\$sdkVersion\ucrt"),
    (Join-Path $sdkRoot "Include\$sdkVersion\um"),
    (Join-Path $sdkRoot "Include\$sdkVersion\shared")
) -join ';'
$env:LIB = @(
    (Join-Path $msvc 'lib\x64'),
    (Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64"),
    (Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")
) -join ';'

$outRoot = Join-Path $repo 'build\windows\native\x64'
Reset-Directory -Path $outRoot -Label 'build/windows/native/x64'

$codecAbi = Join-Path $repo 'src\shared\codec\abi'
$stb = Join-Path $repo 'src\shared\codec\third_party\stb'
$libwebp = Join-Path $repo 'src\shared\codec\third_party\libwebp'
$nanosvg = Join-Path $repo 'src\shared\codec\third_party\nanosvg'
$tinyexr = Join-Path $repo 'src\shared\codec\third_party\tinyexr'
$miniz = Join-Path $repo 'src\shared\codec\third_party\miniz'
$bcdec = Join-Path $repo 'src\shared\codec\third_party\bcdec'
$openjpeg = Join-Path $repo 'src\shared\codec\third_party\openjpeg'
$thirdParty = Join-Path $repo 'src\shared\codec\third_party'
$dav1d = Join-Path $thirdParty 'dav1d'
$libjxl = Join-Path $thirdParty 'libjxl'
$highway = Join-Path $thirdParty 'highway'
$filesAbi = Join-Path $repo 'src\shared\filelist\abi'

# dav1d keeps the Meson source groups: the bitdepth templates are compiled once
# per bit depth, everything else once. Assembly and the per-architecture CPU
# files stay out because this build targets the portable C paths only.
$dav1dSources = @(
    (Join-Path $dav1d 'src\cdf.c'),
    (Join-Path $dav1d 'src\cpu.c'),
    (Join-Path $dav1d 'src\data.c'),
    (Join-Path $dav1d 'src\decode.c'),
    (Join-Path $dav1d 'src\dequant_tables.c'),
    (Join-Path $dav1d 'src\getbits.c'),
    (Join-Path $dav1d 'src\intra_edge.c'),
    (Join-Path $dav1d 'src\itx_1d.c'),
    (Join-Path $dav1d 'src\lf_mask.c'),
    (Join-Path $dav1d 'src\lib.c'),
    (Join-Path $dav1d 'src\log.c'),
    (Join-Path $dav1d 'src\mem.c'),
    (Join-Path $dav1d 'src\msac.c'),
    (Join-Path $dav1d 'src\obu.c'),
    (Join-Path $dav1d 'src\pal.c'),
    (Join-Path $dav1d 'src\picture.c'),
    (Join-Path $dav1d 'src\qm.c'),
    (Join-Path $dav1d 'src\ref.c'),
    (Join-Path $dav1d 'src\refmvs.c'),
    (Join-Path $dav1d 'src\scan.c'),
    (Join-Path $dav1d 'src\tables.c'),
    (Join-Path $dav1d 'src\thread_task.c'),
    (Join-Path $dav1d 'src\warpmv.c'),
    (Join-Path $dav1d 'src\wedge.c'),
    (Join-Path $dav1d 'src\win32\thread.c')
)
$dav1dTemplateSources = @(
    (Join-Path $dav1d 'src\cdef_apply_tmpl.c'),
    (Join-Path $dav1d 'src\cdef_tmpl.c'),
    (Join-Path $dav1d 'src\fg_apply_tmpl.c'),
    (Join-Path $dav1d 'src\filmgrain_tmpl.c'),
    (Join-Path $dav1d 'src\ipred_prepare_tmpl.c'),
    (Join-Path $dav1d 'src\ipred_tmpl.c'),
    (Join-Path $dav1d 'src\itx_tmpl.c'),
    (Join-Path $dav1d 'src\lf_apply_tmpl.c'),
    (Join-Path $dav1d 'src\loopfilter_tmpl.c'),
    (Join-Path $dav1d 'src\looprestoration_tmpl.c'),
    (Join-Path $dav1d 'src\lr_apply_tmpl.c'),
    (Join-Path $dav1d 'src\mc_tmpl.c'),
    (Join-Path $dav1d 'src\recon_tmpl.c')
)
$dav1dIncludes = @(
    (Join-Path $dav1d 'include\compat\msvc'),
    (Join-Path $dav1d 'include'),
    (Join-Path $dav1d 'include\dav1d'),
    (Join-Path $dav1d 'src'),
    $dav1d
)
$dav1dWarnings = @('4090')
$libjxlWarnings = @(
    '4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459',
    '4701', '4702', '4706', '4146', '4018', '4133', '4189', '4819',
    '4273', '4127', '4245', '4389', '4334', '4703', '4099', '4333',
    '4324', '4619', '4623', '4625', '4626', '4668', '4820', '5026',
    '5027', '5054', '5105', '5219', '5220', '5234', '5264', '5266', '4806'
)
$highwayWarnings = @(
    '4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459',
    '4701', '4702', '4706', '4146', '4018', '4133', '4189', '4819',
    '4273', '4127', '4245', '4389', '4334', '4703', '4099', '4333'
)
$libheifWarnings = @(
    '4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459',
    '4701', '4702', '4706', '4146', '4018', '4133', '4189', '4819',
    '4273', '4127', '4245', '4389', '4334', '4703', '4099', '4333'
)

# Each library names its own source file and the directories it includes.
$targets = @(
    @{
        Name = 'router'
        Sources = @(
            'src\shared\codec\router\router.c',
            'src\shared\codec\router\router_loader.c',
            'src\shared\codec\router\router_io.c',
            'src\shared\codec\router\router_cache.c',
            'src\shared\codec\router\router_platform.c'
        )
        Includes = @($codecAbi)
        Exports = 'NV_ROUTER_ABI_EXPORTS'
    },
    @{
        Name = 'files'
        Source = 'src\shared\filelist\files.c'
        Includes = @($filesAbi)
        Exports = 'NV_FILES_ABI_EXPORTS'
    },
    @{
        Name = 'png_codec'
        Source = 'src\shared\codec\formats\png\png_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'jpg_codec'
        Source = 'src\shared\codec\formats\jpg\jpg_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'gif_codec'
        Source = 'src\shared\codec\formats\gif\gif_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'bmp_codec'
        Source = 'src\shared\codec\formats\bmp\bmp_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'ico_codec'
        Source = 'src\shared\codec\formats\ico\ico_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'webp_codec'
        Source = 'src\shared\codec\formats\webp\webp_codec.c'
        Includes = @($codecAbi, (Join-Path $libwebp 'src'), $libwebp)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Defines = @('NDEBUG')
        Vendored = @(
            (Join-Path $libwebp 'src\dec\*.c'),
            (Join-Path $libwebp 'src\demux\*.c'),
            (Join-Path $libwebp 'src\dsp\*.c'),
            (Join-Path $libwebp 'src\utils\*.c')
        )
    },
    @{
        Name = 'svg_codec'
        Source = 'src\shared\codec\formats\svg\svg_codec.cpp'
        Includes = @($codecAbi, $nanosvg, $miniz)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Language = 'cpp'
        Vendored = @((Join-Path $miniz 'miniz.c'))
        Suppress = @('4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459', '4701', '4702', '4706')
    },
    @{
        Name = 'tga_codec'
        Source = 'src\shared\codec\formats\tga\tga_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'exr_codec'
        Source = 'src\shared\codec\formats\exr\exr_codec.cpp'
        Includes = @($codecAbi, $tinyexr, $miniz)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Language = 'cpp'
        Suppress = @('4244', '4267', '4245', '4305', '4100', '4101', '4456', '4457', '4459', '4701', '4702', '4706')
        Vendored = @((Join-Path $miniz '*.c'))
    },
    @{
        Name = 'tiff_codec'
        Source = 'src\shared\codec\formats\tiff\tiff_codec.c'
        Includes = @($codecAbi, $miniz)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Vendored = @((Join-Path $miniz '*.c'))
    },
    @{
        Name = 'hdr_codec'
        Source = 'src\shared\codec\formats\hdr\hdr_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'dds_codec'
        Source = 'src\shared\codec\formats\dds\dds_codec.c'
        Includes = @($codecAbi, $bcdec)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Suppress = @('4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459', '4701', '4702', '4706')
    },
    @{
        Name = 'psd_codec'
        Source = 'src\shared\codec\formats\psd\psd_codec.c'
        Includes = @($codecAbi, $miniz)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Vendored = @((Join-Path $miniz '*.c'))
    },
    @{
        Name = 'dng_codec'
        Source = 'src\shared\codec\formats\dng\dng_codec.c'
        Includes = @($codecAbi, $stb)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
    },
    @{
        Name = 'jp2_codec'
        Source = 'src\shared\codec\formats\jp2\jp2_codec.c'
        Includes = @($codecAbi, $openjpeg)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Defines = @('OPJ_STATIC')
        Vendored = @((Join-Path $openjpeg '*.c'))
    },
    @{
        Name = 'heic_codec'
        Source = 'src\shared\codec\formats\heic\heic_codec.c'
        Includes = @($codecAbi, $thirdParty)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Language = 'cpp'
        Defines = @(
            'HAVE_LIBDE265=1',
            'LIBHEIF_EXPORTS',
            'LIBHEIF_STATIC_BUILD',
            'NDEBUG'
        )
        Suppress = @('4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459', '4701', '4702', '4706')
        # libheif owns the container, colour conversion and the libde265 plugin;
        # libde265 supplies the HEVC decoder. Both are C++ and vendor their own
        # warning baseline. SIMD, plugins and every codec backend other than
        # libde265 stay disabled by leaving their HAVE_ macros undefined.
        Vendored = @(
            (Join-Path $thirdParty 'libheif\*.cpp'),
            (Join-Path $thirdParty 'libheif\color-conversion\*.cpp'),
            (Join-Path $thirdParty 'libheif\plugins\decoder_libde265.cpp'),
            (Join-Path $thirdParty 'libheif\plugins\encoder_mask.cpp'),
            (Join-Path $thirdParty 'libde265\*.cpp'),
            (Join-Path $thirdParty 'extra\win32cond.c')
        )
        VendoredLanguage = 'cpp'
        VendoredDefines = @(
            'HAVE_LIBDE265=1', 'LIBHEIF_EXPORTS', 'LIBHEIF_STATIC_BUILD',
            'LIBDE265_STATIC_BUILD', 'NDEBUG'
        )
        VendoredSuppress = @(
            '4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459',
            '4701', '4702', '4706', '4146', '4018', '4133', '4189', '4819',
            '4273', '4127', '4245', '4389', '4334', '4703', '4099', '4333'
        )
    },
    @{
        Name = 'avif_codec'
        Source = 'src\shared\codec\formats\avif\avif_codec.c'
        Includes = @($codecAbi, $thirdParty)
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Language = 'cpp'
        Defines = @(
            'HAVE_DAV1D=1',
            'LIBHEIF_EXPORTS',
            'LIBHEIF_STATIC_BUILD',
            'NDEBUG'
        )
        Suppress = @('4244', '4267', '4305', '4100', '4101', '4456', '4457', '4459', '4701', '4702', '4706')
        # AVIF uses the same libheif container reader as HEIC, but the pixels
        # come from the dav1d plugin. libheif core, the plugin and dav1d are
        # kept as separate compile groups because dav1d compiles its bitdepth
        # templates twice with different definitions.
        VendoredSets = @(
            @{
                Name = 'libheif'
                Sources = @(
                    (Join-Path $thirdParty 'libheif\*.cpp'),
                    (Join-Path $thirdParty 'libheif\color-conversion\*.cpp'),
                    (Join-Path $thirdParty 'libheif\plugins\encoder_mask.cpp')
                )
                Language = 'cpp'
                Defines = @('HAVE_DAV1D=1', 'LIBHEIF_EXPORTS', 'LIBHEIF_STATIC_BUILD', 'NDEBUG')
                Suppress = $libheifWarnings
            },
            @{
                Name = 'plugin'
                Sources = @((Join-Path $thirdParty 'libheif\plugins\decoder_dav1d.cpp'))
                Includes = @($thirdParty) + $dav1dIncludes
                Language = 'cpp'
                Defines = @('HAVE_DAV1D=1', 'LIBHEIF_STATIC_BUILD', 'NDEBUG')
                Suppress = $libheifWarnings
            },
            @{
                Name = 'dav1d8'
                Sources = $dav1dTemplateSources
                Includes = $dav1dIncludes
                Language = 'c'
                Defines = @('BITDEPTH=8')
                Suppress = $dav1dWarnings
            },
            @{
                Name = 'dav1d16'
                Sources = $dav1dTemplateSources
                Includes = $dav1dIncludes
                Language = 'c'
                Defines = @('BITDEPTH=16')
                Suppress = $dav1dWarnings
            },
            @{
                Name = 'dav1d'
                Sources = $dav1dSources
                Includes = $dav1dIncludes
                Language = 'c'
                Suppress = $dav1dWarnings
            }
        )
    },
    @{
        Name = 'jxl_codec'
        Source = 'src\shared\codec\formats\jxl\jxl_codec.c'
        Includes = @(
            $codecAbi,
            (Join-Path $libjxl 'lib\include'),
            (Join-Path $libjxl 'include'),
            $libjxl,
            $highway
        )
        Exports = 'NV_FORMAT_ABI_EXPORTS'
        Language = 'cpp'
        Defines = @(
            'JXL_STATIC_DEFINE',
            'JXL_INTERNAL_LIBRARY_BUILD',
            'HWY_STATIC_DEFINE',
            'JPEGXL_ENABLE_TRANSCODE_JPEG=0',
            'JPEGXL_ENABLE_BOXES=0',
            'NDEBUG'
        )
        # libjxl's decoder library is made of the core decoder translation
        # units and the decoder-only C API. The JPEG reconstruction and
        # box-content paths are compiled out, which also removes the Brotli
        # dependency.
        VendoredSets = @(
            @{
                Name = 'jxl'
                Sources = @(
                    (Join-Path $libjxl 'lib\jxl\*.cpp'),
                    (Join-Path $libjxl 'lib\jxl\modular\*.cpp'),
                    (Join-Path $libjxl 'lib\jxl\modular\encoding\*.cpp'),
                    (Join-Path $libjxl 'lib\jxl\modular\transform\*.cpp'),
                    (Join-Path $libjxl 'lib\jxl\render_pipeline\*.cpp')
                )
                Language = 'cpp'
                Flags = @('/bigobj')
                Suppress = $libjxlWarnings
            },
            @{
                Name = 'highway'
                Sources = @((Join-Path $highway 'hwy\*.cpp'))
                Includes = @($highway)
                Language = 'cpp'
                Defines = @('HWY_STATIC_DEFINE', 'NDEBUG')
                Flags = @('/bigobj')
                Suppress = $highwayWarnings
            }
        )
    }
)

Write-Host "==> Native build (x64)"
Write-Host "    msvc: $msvc"
Write-Host "    sdk : $sdkVersion"

$link = Join-Path $msvc 'bin\Hostx64\x64\link.exe'

# Runs a toolchain step as a child process so its output can be reprinted
# whatever the exit code, and fails the build when the step does.
function Invoke-Toolchain {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Tool
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    foreach ($line in (($stdout + $stderr) -split "`r?`n")) {
        if ($line) { Write-Host "    $line" }
    }
    if ($process.ExitCode -ne 0) {
        throw "$Label failed with exit code $($process.ExitCode)."
    }
}

foreach ($target in $targets) {
    # A target either names one source or a list of them; every unit is
    # compiled separately and then linked into one library.
    $sources = @()
    foreach ($relative in ($target.ContainsKey('Sources') ? $target.Sources : @($target.Source))) {
        $source = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Source not found: $source"
        }
        $sources += $source
    }

    Write-Host "==> $($target.Name)"

    $commonIncludes = @()
    foreach ($include in $target.Includes) {
        $commonIncludes += "/I$include"
    }

    $objects = @()

    # The project's own source is held to the strict warning policy.
    $language = $target.ContainsKey('Language') ? $target.Language : 'c'
    $languageFlags = @('/std:c11')
    if ($language -eq 'cpp') {
        $languageFlags = @('/std:c++17', '/EHsc')
    }
    $sourceIndex = 0
    foreach ($source in $sources) {
        $objectFile = Join-Path $outRoot "$($target.Name)_$sourceIndex.obj"
        $arguments = @('/nologo', '/c', '/O2', '/W4', '/WX')
        $arguments += $languageFlags
        if ($target.ContainsKey('Flags')) {
            $arguments += $target.Flags
        }
        $arguments += '/D_CRT_SECURE_NO_WARNINGS'
        $arguments += "/D$($target.Exports)"
        foreach ($define in ($target.ContainsKey('Defines') ? $target.Defines : @())) {
            $arguments += "/D$define"
        }
        foreach ($code in ($target.ContainsKey('Suppress') ? $target.Suppress : @())) {
            $arguments += "/wd$code"
        }
        $arguments += $commonIncludes
        $arguments += "/Fo$objectFile"
        $arguments += $source
        Invoke-Toolchain -Tool $cl -Arguments $arguments -Label "cl ($($target.Name))"
        $objects += $objectFile
        $sourceIndex++
    }
    # Vendored code keeps its own warning baseline; its warnings are not
    # something this repository can fix, but the build still runs warning free.
    # A target lists either one Vendored set with shared flags, or named
    # VendoredSets that differ per group (dav1d compiles the same bitdepth
    # templates twice with different definitions).
    $vendoredSets = @()
    if ($target.ContainsKey('VendoredSets')) {
        foreach ($set in $target.VendoredSets) {
            $vendoredSets += $set
        }
    }
    elseif ($target.ContainsKey('Vendored')) {
        $vendoredSets += @{
            Name = 'vendor'
            Sources = $target.Vendored
            Language = $target.ContainsKey('VendoredLanguage') ? $target.VendoredLanguage : 'c'
            Defines = $target.ContainsKey('VendoredDefines') ? $target.VendoredDefines : @()
            Suppress = $target.ContainsKey('VendoredSuppress') ? $target.VendoredSuppress : @()
        }
    }

    foreach ($set in $vendoredSets) {
        $vendored = @()
        foreach ($pattern in $set.Sources) {
            $vendored += Get-ChildItem -Path $pattern -File | ForEach-Object { $_.FullName }
        }

        $vendoredLanguage = $set.ContainsKey('Language') ? $set.Language : 'c'
        $vendoredFlags = $vendoredLanguage -eq 'cpp' ? @('/std:c++17', '/EHsc') : @('/std:c11')
        $vendoredIncludes = @($commonIncludes)
        if ($set.ContainsKey('Includes')) {
            foreach ($include in $set.Includes) {
                $vendoredIncludes += "/I$include"
            }
        }
        $vendoredDefines = $set.ContainsKey('Defines') ? $set.Defines : @()
        $index = 0
        foreach ($vendoredSource in $vendored) {
            $vendoredObject = Join-Path $outRoot "$($target.Name)_vendor_$($set.Name)_$index.obj"
            $arguments = @(
                '/nologo', '/c', '/O2', '/W3', '/D_CRT_SECURE_NO_WARNINGS'
            )
            $arguments += $vendoredFlags
            if ($set.ContainsKey('Flags')) {
                $arguments += $set.Flags
            }
            foreach ($define in ($target.ContainsKey('Defines') ? $target.Defines : @())) {
                $arguments += "/D$define"
            }
            foreach ($define in $vendoredDefines) {
                $arguments += "/D$define"
            }
            # Vendored C code keeps the original fixed baseline and adds the
            # codes the set declares. C++ sets declare their complete list.
            if ($vendoredLanguage -eq 'cpp') {
                foreach ($code in ($set.ContainsKey('Suppress') ? $set.Suppress : @())) {
                    $arguments += "/wd$code"
                }
            }
            else {
                foreach ($code in @('4244', '4267', '4100', '4456', '4457', '4459',
                                    '4701', '4702', '4706', '4146', '4018', '4133',
                                    '4189', '4819')) {
                    $arguments += "/wd$code"
                }
                foreach ($code in ($set.ContainsKey('Suppress') ? $set.Suppress : @())) {
                    $arguments += "/wd$code"
                }
            }
            $arguments += $vendoredIncludes
            $arguments += "/Fo$vendoredObject"
            $arguments += $vendoredSource
            Invoke-Toolchain -Tool $cl -Arguments $arguments -Label "cl ($($target.Name) $($set.Name))"
            $objects += $vendoredObject
            $index++
        }
    }

    $library = Join-Path $outRoot "$($target.Name).dll"
    $arguments = @('/nologo', '/DLL', '/INCREMENTAL:NO', "/OUT:$library")
    $arguments += $objects
    Invoke-Toolchain -Tool $link -Arguments $arguments -Label "link ($($target.Name))"
}

# The compiler also leaves import libraries and object files behind; only the
# dynamic libraries are shipped, so the rest is dropped.
Get-ChildItem -LiteralPath $outRoot -File |
    Where-Object { $_.Extension -in '.obj', '.lib', '.exp' } |
    Remove-Item -Force

if ($Install) {
    $target = Join-Path $repo 'src\windows\Native\x64'
    foreach ($dll in Get-ChildItem -LiteralPath $outRoot -Filter '*.dll') {
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $target $dll.Name) -Force
        Write-Host "    installed $($dll.Name)"
    }
}

Write-Host "==> Native build finished"
Write-Host "    $outRoot"
