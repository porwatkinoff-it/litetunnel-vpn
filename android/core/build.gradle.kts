import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Platform-free logic (links, subscriptions, config, state), so the tests
// run on the JVM without an emulator. Mirrors Packages/LiteKit.
plugins {
    id("org.jetbrains.kotlin.jvm")
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
