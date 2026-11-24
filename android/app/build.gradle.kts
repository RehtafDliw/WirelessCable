import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
    id("com.google.devtools.ksp")
}

/*
 * 发布签名（正式包必须签，不能拿 debug 包冒充）。
 *
 * 密钥与口令放在项目根的 keystore.properties（不进交付目录、不在仓库里公开）：
 *   storeFile=keys/wirelesscable-release.jks
 *   storePassword=… / keyAlias=… / keyPassword=…
 * 丢了这份密钥就无法给已安装的用户升级，必须和发布包一起备份。
 */
val keystoreProps = Properties().apply {
    val file = rootProject.file("../keystore.properties")
    if (file.exists()) {
        file.inputStream().use { load(it) }
    }
}

/*
 * 版本号**唯一来源**是项目根的 `Directory.Build.props`（桌面端也读它）。
 *
 * 以前这里手写 `versionCode = 353` + `versionName = "2.11.1"`，每次发版都要
 * 记得同时改三处（props 的 Version、这里的 versionName、这里的 versionCode），
 * 漏一处就出现"桌面 2.11.2、手机 2.11.1"或者 versionCode 没涨导致装不上。
 * 现在只改 props 一处，versionCode 由版本**派生**，永远不会忘、不会撞。
 */
val appVersion: String = run {
    val props = rootProject.file("../Directory.Build.props")
    require(props.exists()) { "缺少版本号来源：${props.absolutePath}" }
    Regex("<Version>([^<]+)</Version>")
        .find(props.readText())
        ?.groupValues?.get(1)
        ?: error("在 Directory.Build.props 里找不到 <Version>")
}

/**
 * Android 要求新包的 `versionCode` **严格大于**已装的那个，否则系统拒绝升级
 * （报"应用未安装"）。所以它必须能从一个可读的版本号稳定算出来。
 *
 * 规则：`major * 10_000_000 + minor * 100_000 + patch * 1_000 + 999`
 *
 *   2.11.1 → 21101999
 *   2.11.2 → 21102999
 *   2.12.0 → 21200999
 *   3.0.0  → 30000999
 *
 * 末尾的 999 给预发布版本留出空位（比如 `2.11.1-beta.1` 可以占 21101001），
 * 正式版永远排在它的预发布之后。上限来自 Android 的 2_100_000_000。
 */
val appVersionCode: Int = run {
    val plain = appVersion.substringBefore('-').substringBefore('+')
    val parts = plain.split('.')
    require(parts.size == 3) { "版本号必须是 a.b.c 形式，当前是 $appVersion" }

    val numbers = parts.map { part ->
        part.toIntOrNull() ?: error("版本号里有一段不是数字：'$part'（完整值 $appVersion）")
    }
    val (major, minor, patch) = numbers
    require(major <= 213 && minor <= 99 && patch <= 99) {
        "版本号 $appVersion 超出 Android versionCode 的编码范围（major≤213, minor≤99, patch≤99）"
    }

    major * 10_000_000 + minor * 100_000 + patch * 1_000 + 999
}

android {
    namespace = "com.wirelesscable.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.wirelesscable.app"
        // 实际支持的起点是 Android 11（API 30），不是 13。
        // 13+ 才有的东西（NEARBY_WIFI_DEVICES、specialUse 前台服务类型）在代码里都有
        // 版本分支；12 及以下只有"定位"这一个 Wi-Fi 权限，权限文案也按版本走。
        // 这行以前写的是"需求下限：Android 13"，和取值 30 不一致，排查权限时被误导过（1.5.58）。
        minSdk = 30
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
    }

    /**
     * 契约表（仓库根 `contracts/`）打进 assets。
     *
     * 桌面端把它嵌进程序集、前端在构建时 import 它，手机端读 assets——
     * **三处同一份文件**。改状态机语义只改那一份，三端测试同时红。
     */
    sourceSets["main"].assets.srcDir(rootProject.file("../contracts"))

    signingConfigs {
        create("release") {
            if (keystoreProps.isNotEmpty()) {
                storeFile = rootProject.file("../" + keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")

                // 用 AGP 默认方案（v1 关、v2 开）：实测只带 v3 的包在 ColorOS 上被
                // PackageManager 以 -99 拒绝；v2 是 Android 11+ 对 targetSdk≥30 的硬要求。
            }
        }
    }

    testOptions {
        // 直连的分段下载要在 JVM 里真跑一遍（起一个本地 HTTPS 服务端，故意在半个块上断流），
        // 需要 android.util.Log / SystemClock 这些桩返回默认值而不是抛异常。
        unitTests.isReturnDefaultValues = true
    }

    buildTypes {
        release {
            // 不压缩/混淆：Compose + 反射点少，但发布包里多几 MB 换来"行为与 debug 完全一致"
            isMinifyEnabled = false
            isShrinkResources = false
            isDebuggable = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        // 设置页显示版本号时读 BuildConfig.VERSION_NAME，避免像以前那样写死
        buildConfig = true
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }

    // 只保留真机 ABI，减小 APK
    defaultConfig {
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a")
        }
    }
}

// 正式包必须签名：没有 keystore.properties 就直接失败，避免"打了个没签名的包当发布物"
tasks.matching { it.name == "packageRelease" }.configureEach {
    doFirst {
        if (keystoreProps.isEmpty()) {
            throw GradleException(
                "缺少 keystore.properties：正式包必须签名。请把发布密钥放到项目根并填写口令。",
            )
        }
    }
}

// 把 -Dwc.* 透传给单元测试 JVM，供真实 HTTPS 联调测试使用
tasks.withType<Test>().configureEach {
    System.getProperties()
        .mapKeys { it.key.toString() }
        .filterKeys { it.startsWith("wc.") }
        .forEach { (key, value) -> systemProperty(key, value) }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.03")
    implementation(composeBom)

    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")

    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.6")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")

    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    // 扫码配对：CameraX 预览 + ZXing 解码（不依赖 Google Play Services，纯离线）
    implementation("androidx.camera:camera-core:1.4.2")
    implementation("androidx.camera:camera-camera2:1.4.2")
    implementation("androidx.camera:camera-lifecycle:1.4.2")
    implementation("androidx.camera:camera-view:1.4.2")
    implementation("com.google.zxing:core:3.5.3")

    implementation("androidx.room:room-runtime:2.6.1")
    implementation("androidx.room:room-ktx:2.6.1")
    ksp("androidx.room:room-compiler:2.6.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.8.1")

    debugImplementation("androidx.compose.ui:ui-tooling")
}
