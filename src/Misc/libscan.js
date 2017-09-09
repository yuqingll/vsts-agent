var p = require('child_process');
var fs = require('fs');

var wellKnownDependencies = ['libc\.so\.\d', 'libm\.so\.\d', 'libstdc++\.so\.\d', 'libpthread\.so\.\d', 'linux-vdso\.so\.\d', 'libgcc_s\.so\.\d', 'librt\.so\.\d', 'libdl\.so\.\d', 'ld-linux-x86-64.so\.\d', 'libcom_err\.so\.2', 'libcrypt\.so\.1', 'libgpg-error\.so\.0', 'liblzma\.so\.5', 'libuuid\.so\.1'];
var dependencyScanQueue = [];
var currentIndex = 0;

var ldd = function (file) {
    var cl = `ldd "${file}"`;
    var output;
    try {
        output = p.execSync(cl);
    }
    catch (err) {
        throw new Error(`The following command line failed: '${cl}'`);
    }

    output = (output || '').toString().trim();

    var outputs = output.split("\n");
    var deps = [];
    for (var i = 0; i < outputs.length; i++) {
        var dep_path = outputs[i].replace("\t", "").replace(/\(0x[a-z0-9]+\)$/gi, "").trim();
        var dep;
        var path;

        // linux-vdso.so.1 =>
        // libdl.so.2 => /lib64/libdl.so.2
        // /lib64/ld-linux-x86-64.so.2
        if (dep_path.indexOf('=>') != -1) {
            var splited = dep_path.split('=>', 2);
            if (splited[1].length == 0) {
                console.log('Skip kernel library: ' + dep_path);
            } else {
                dep = splited[0].trim();
                path = splited[1].trim();
            }
        } else {
            console.log('Skip hardcoded library: ' + dep_path);
        }

        if (dep) {
            if (!skip(dep)) {
                deps.push(path);
            } else {
                console.log(`Skip: '${dep}'`);
            }
        }
    }

    console.log(`Detected ${deps.length} dependencies from ${file}`);
    return deps;
}

var skip = function (file) {
    for (var i = 0; i < wellKnownDependencies.length; i++) {
        if (file.match(wellKnownDependencies[i])) {
            return true;
        }
    }
}

process.argv.forEach((arg) => {
    console.log('Scan dependencies for: ' + arg);
    var ret = ldd(arg);
    dependencyScanQueue.push(ret);
})


while (currentIndex < dependencyScanQueue.length) {
    var candidate = dependencyScanQueue[currentIndex];
    console.log('Scan dependencies for: ' + candidate);

    var dependencies = ldd(candidate);
    dependencies.forEach((dep) => {
        if (dependencyScanQueue.indexOf(dep) == -1) {
            console.log('Add new dependency for: ' + dep);
            dependencyScanQueue.push(dep);
        }
    });

    currentIndex++;
}

console.log('-------------BEGIN DEPENDENCIES-------------');
for (var i = 0; i < dependencyScanQueue.length; i++) {
    console.log(dependencyScanQueue[i]);
}
console.log('-------------END DEPENDENCIES-------------');