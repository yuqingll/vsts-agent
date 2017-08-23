#!/bin/bash

###############################################################################
#  
#  ./dev.sh build/layout/test/package [Debug/Release]
#
###############################################################################

DEV_CMD=$1
DEV_CONFIG=$2

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LAYOUT_DIR="$SCRIPT_DIR/../_layout"
DOWNLOAD_DIR="$SCRIPT_DIR/../_downloads"
DOTNETSDK_ROOT="$SCRIPT_DIR/../_dotnetsdk"
DOTNETSDK_VERSION="1.0.1"
DOTNETSDK_INSTALLDIR="$DOTNETSDK_ROOT/$DOTNETSDK_VERSION"

pushd $SCRIPT_DIR

BUILD_CONFIG="Debug"
if [[ "$DEV_CONFIG" == "Release" ]]; then
    BUILD_CONFIG="Release"
fi

PLATFORM_NAME=`uname`
PLATFORM="windows"
if [[ ("$PLATFORM_NAME" == "Linux") || ("$PLATFORM_NAME" == "Darwin") ]]; then
    PLATFORM=`echo "${PLATFORM_NAME}" | awk '{print tolower($0)}'`
fi

# allow for #if defs in code
define_os='OS_WINDOWS'
runtime_id='win-x64'
if [[ "$PLATFORM" == 'linux' ]]; then
   define_os='OS_LINUX'
   runtime_id='linux-x64'
elif [[ "$PLATFORM" == 'darwin' ]]; then
   define_os='OS_OSX'
   runtime_id='osx-x64'
fi

WINDOWSAGENTSERVICE_PROJFILE="Agent.Service/Windows/AgentService.csproj"
WINDOWSAGENTSERVICE_BIN="Agent.Service/Windows/bin/Debug"

function failed()
{
   local error=${1:-Undefined error}
   echo "Failed: $error" >&2
   popd
   exit 1
}

function warn()
{
   local error=${1:-Undefined error}
   echo "WARNING - FAILED: $error" >&2
}

function heading()
{
    echo
    echo
    echo -----------------------------------------
    echo   ${1}
    echo -----------------------------------------
}

function build ()
{
    dotnet msbuild //t:Build //p:PackageRuntime=${runtime_id} //p:BUILDCONFIG=${BUILD_CONFIG} || failed build
    
    if [[ "$CURRENT_PLATFORM" == 'windows' ]]; then
        reg_out=`reg query "HKLM\SOFTWARE\Microsoft\MSBuild\ToolsVersions\4.0" -v MSBuildToolsPath`
        msbuild_location=`echo $reg_out | tr -d '\r\n' | tr -s ' ' | cut -d' ' -f5 | tr -d '\r\n'`
              
        local rc=$?
        if [ $rc -ne 0 ]; then
            failed "Can not find msbuild location, failing build"
        fi
    fi

    if [[ "$define_os" == 'OS_WINDOWS' && "$msbuild_location" != "" ]]; then
        $msbuild_location/msbuild.exe $WINDOWSAGENTSERVICE_PROJFILE || failed "msbuild AgentService.csproj"
    fi
}

function layout ()
{
    # layout
    dotnet msbuild //t:layout //p:PackageRuntime=${runtime_id} //p:BUILDCONFIG=${BUILD_CONFIG} || failed build

    # if [[ "$define_os" == 'OS_WINDOWS' ]]; then
    #     # TODO Make sure to package Release build instead of debug build
    #     echo Copying Agent.Service
    #     cp -Rf $WINDOWSAGENTSERVICE_BIN/* ${LAYOUT_DIR}/bin
    # fi

    #change execution flag to allow running with sudo
    if [[ "$PLATFORM" == 'linux' ]]; then
        chmod +x ${LAYOUT_DIR}/bin/Agent.Listener
        chmod +x ${LAYOUT_DIR}/bin/Agent.Worker
    fi

    heading Externals ...
    bash ./Misc/externals.sh $PLATFORM || checkRC externals.sh
}

function runtest ()
{
    if [[ ("$PLATFORM" == "linux") || ("$PLATFORM" == "darwin") ]]; then
        ulimit -n 1024
    fi

    heading Testing ...
    export VSTS_AGENT_SRC_DIR=${SCRIPT_DIR}
    dotnet msbuild //t:test //p:PackageRuntime=${runtime_id} //p:BUILDCONFIG=${BUILD_CONFIG} || failed "failed tests" 
}

function package ()
{
    # get the runtime we are build for
    # if exist Agent.Listener/bin/${BUILD_CONFIG}/netcoreapp1.1
    build_folder="Agent.Listener/bin/${BUILD_CONFIG}/netcoreapp1.1"
    if [ ! -d "${build_folder}" ]; then
        echo "You must build first.  Expecting to find ${build_folder}"
    fi

    pushd "${build_folder}" > /dev/null
    pwd
    runtime_folder=`ls -d */`

    pkg_runtime=${runtime_folder%/}
    popd > /dev/null

    pkg_dir=`pwd`/../_package

    agent_ver=`${LAYOUT_DIR}/bin/Agent.Listener --version` || failed "version"
    agent_pkg_name="vsts-agent-${pkg_runtime}-${agent_ver}"
    # -$(date +%m)$(date +%d)"

    heading "Packaging ${agent_pkg_name}"

    rm -Rf ${LAYOUT_DIR}/_diag
    find ${LAYOUT_DIR}/bin -type f -name '*.pdb' -delete
    mkdir -p $pkg_dir
    pushd $pkg_dir > /dev/null
    rm -Rf *

    if [[ ("$PLATFORM" == "linux") || ("$PLATFORM" == "darwin") ]]; then
        tar_name="${agent_pkg_name}.tar.gz"
        echo "Creating $tar_name in ${LAYOUT_DIR}"
        tar -czf "${tar_name}" -C ${LAYOUT_DIR} .
    elif [[ ("$PLATFORM" == "windows") ]]; then
        zip_name="${agent_pkg_name}.zip"
        echo "Convert ${LAYOUT_DIR} to Windows style path"
        window_path=${LAYOUT_DIR:1}
        window_path=${window_path:0:1}:${window_path:1}
        echo "Creating $zip_name in ${window_path}"
        powershell -NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "Add-Type -Assembly \"System.IO.Compression.FileSystem\"; [System.IO.Compression.ZipFile]::CreateFromDirectory(\"${window_path}\", \"${zip_name}\")"
    fi

    popd > /dev/null
}

if [[ (! -d "${DOTNETSDK_INSTALLDIR}") || (! -e "${DOTNETSDK_INSTALLDIR}/.${DOTNETSDK_VERSION}") || (! -e "${DOTNETSDK_INSTALLDIR}/dotnet") ]]; then
    
    # Download dotnet SDK to ../_dotnetsdk directory
    heading "Ensure Dotnet SDK"

    # _dotnetsdk
    #           \1.0.x
    #                            \dotnet
    #                            \.1.0.x
    echo "Download dotnetsdk into ${DOTNETSDK_INSTALLDIR}"
    rm -Rf ${DOTNETSDK_DIR}

    # run dotnet-install.ps1 on windows, dotnet-install.sh on linux
    if [[ ("$PLATFORM" == "windows") ]]; then
        echo "Convert ${DOTNETSDK_INSTALLDIR} to Windows style path"
        sdkinstallwindow_path=${DOTNETSDK_INSTALLDIR:1}
        sdkinstallwindow_path=${sdkinstallwindow_path:0:1}:${sdkinstallwindow_path:1}
        powershell -NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "& \"./Misc/dotnet-install.ps1\" -Version ${DOTNETSDK_VERSION} -InstallDir \"${sdkinstallwindow_path}\" -NoPath; exit $LastExitCode;" || checkRC dotnet-install.ps1
    else
        bash ./Misc/dotnet-install.sh --version ${DOTNETSDK_VERSION} --install-dir ${DOTNETSDK_INSTALLDIR} --no-path || checkRC dotnet-install.sh
    fi

    echo "${DOTNETSDK_VERSION}" > ${DOTNETSDK_INSTALLDIR}/.${DOTNETSDK_VERSION}
fi

echo "Prepend ${DOTNETSDK_INSTALLDIR} to %PATH%"
export PATH=${DOTNETSDK_INSTALLDIR}:$PATH

heading "Dotnet SDK Version"
dotnet --version

case $DEV_CMD in
   "build") build;;
   "b") build;;
   "test") runtest;;
   "t") runtest;;
   "layout") layout;;
   "l") layout;;
   "package") package;;
   "p") package;;
   *) echo "Invalid cmd.  Use build, test, or layout";;
esac

popd
echo
echo Done.
echo
