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
PACKAGE_DIR="$SCRIPT_DIR/../_package"
DOTNETSDK_ROOT="$SCRIPT_DIR/../_dotnetsdk"
DOTNETSDK_VERSION="2.0.0"
DOTNETSDK_INSTALLDIR="$DOTNETSDK_ROOT/$DOTNETSDK_VERSION"

pushd $SCRIPT_DIR

BUILD_CONFIG="Debug"
if [[ "$DEV_CONFIG" == "Release" ]]; then
    BUILD_CONFIG="Release"
fi

CURRENT_PLATFORM="windows"
if [[ (`uname` == "Linux") || (`uname` == "Darwin") ]]; then
    CURRENT_PLATFORM=`echo \`uname\` | awk '{print tolower($0)}'`
fi

RUNTIME_ID='win-x64'
if [[ "$CURRENT_PLATFORM" == 'linux' ]]; then
   RUNTIME_ID='linux-x64'
elif [[ "$CURRENT_PLATFORM" == 'darwin' ]]; then
   RUNTIME_ID='osx-x64'
fi

WINDOWSAGENTSERVICE_PROJFILE="Agent.Service/Windows/AgentService.csproj"
WINDOWSAGENTSERVICE_BIN="Agent.Service/Windows/bin/$BUILD_CONFIG"

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

function checkRC() {
    local rc=$?
    if [ $rc -ne 0 ]; then
        failed "${1} Failed with return code $rc"
    fi
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
    heading "Building ..."
        
    dotnet_msbuild_args="//p:PackageRuntime=${RUNTIME_ID} //p:BUILDCONFIG=${BUILD_CONFIG}"
    if [[ "$CURRENT_PLATFORM" == 'windows' ]]; then
        vswhere=`find $DOWNLOAD_DIR -name vswhere.exe | head -1`
        vs_location=`$vswhere -latest -property installationPath`
        msbuild_location="$vs_location""\MSBuild\15.0\Bin\msbuild.exe"

        if [[ ! -e "${msbuild_location}" ]]; then
            failed "Can not find msbuild location, failing build"
        fi
        
        msbuild_location_escape="${msbuild_location// /%20}"
        dotnet_msbuild_args="$dotnet_msbuild_args //p:DesktopMSBuild=$msbuild_location_escape"
        # "$msbuild_location" $WINDOWSAGENTSERVICE_PROJFILE //p:Configuration="$BUILD_CONFIG" || failed "msbuild AgentService.csproj"

        # cp -Rf $WINDOWSAGENTSERVICE_BIN/* ${LAYOUT_DIR}/bin
    fi

    dotnet msbuild //t:Build $dotnet_msbuild_args || failed build
}

function layout ()
{
    heading "Create layout ..."

    dotnet msbuild //t:layout //p:PackageRuntime=${RUNTIME_ID} //p:BUILDCONFIG=${BUILD_CONFIG} || failed build

    #change execution flag to allow running with sudo
    if [[ "$CURRENT_PLATFORM" == 'linux' ]]; then
        chmod +x ${LAYOUT_DIR}/bin/Agent.Listener
        chmod +x ${LAYOUT_DIR}/bin/Agent.Worker
    fi

    heading "Setup externals folder for $CURRENT_PLATFORM agent's layout"
    bash ./Misc/externals.sh $CURRENT_PLATFORM || checkRC externals.sh
}

function runtest ()
{
    heading "Testing ..."

    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        ulimit -n 1024
    fi

    export VSTS_AGENT_SRC_DIR=${SCRIPT_DIR}
    dotnet msbuild //t:test //p:PackageRuntime=${RUNTIME_ID} //p:BUILDCONFIG=${BUILD_CONFIG} || failed "failed tests" 
}

function package ()
{
    if [ ! -d "${LAYOUT_DIR}/bin" ]; then
        echo "You must build first.  Expecting to find ${LAYOUT_DIR}/bin"
    fi

    agent_ver=`${LAYOUT_DIR}/bin/Agent.Listener --version` || failed "version"
    agent_pkg_name="vsts-agent-${RUNTIME_ID}-${agent_ver}"

    heading "Packaging ${agent_pkg_name}"

    rm -Rf ${LAYOUT_DIR}/_diag
    find ${LAYOUT_DIR}/bin -type f -name '*.pdb' -delete
    mkdir -p $PACKAGE_DIR
    pushd $PACKAGE_DIR > /dev/null
    rm -Rf *

    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        tar_name="${agent_pkg_name}.tar.gz"
        echo "Creating $tar_name in ${LAYOUT_DIR}"
        tar -czf "${tar_name}" -C ${LAYOUT_DIR} .
    elif [[ ("$CURRENT_PLATFORM" == "windows") ]]; then
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
    if [[ ("$CURRENT_PLATFORM" == "windows") ]]; then
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

heading "Pre-cache external resources for $CURRENT_PLATFORM platform ..."
bash ./Misc/externals.sh $CURRENT_PLATFORM "Pre-Cache" || checkRC "externals.sh Pre-Cache"

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
