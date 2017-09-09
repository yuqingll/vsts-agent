var p = require('child_process');
var fs = require('fs');

var wellKnownDependencies = [
    /libc\.so\.\d/gi,
    /libm\.so\.\d/gi,
    /libstdc\+\+\.so\.\d/gi,
    /libpthread\.so\.\d/gi,
    /linux-vdso\.so\.\d/gi,
    /libgcc_s\.so\.\d/gi,
    /librt\.so\.\d/gi,
    /libdl\.so\.\d/gi,
    /ld-linux-x86-64.so\.\d/gi,
    /libcom_err\.so\.2/gi,
    /libcrypt\.so\.1/gi,
    /libgpg-error\.so\.0/gi,
    /liblzma\.so\.5/gi,
    /libuuid\.so\.1/gi
];
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
        console.log(dep_path);
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

var args = process.argv;
args.splice(0, 2);
args.forEach((arg) => {
    console.log('Scan dependencies for: ' + arg);
    if (!fs.existsSync(arg)) {
        throw new Error('File does not exist: ' + arg);
    }

    var ret = ldd(arg);
    ret.forEach((d) => {
        dependencyScanQueue.push(d);
    });
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