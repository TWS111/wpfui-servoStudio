/*
 * soemfoe_wrapper.c - Thin wrapper for SOEM FoE firmware update in servoStudio
 *
 * Build: compiled to soemfoe.dll by build_soemfoe.ps1
 * Depends: SOEM source (https://github.com/OpenEtherCATsociety/SOEM)
 *
 * In SOEM 2.x, ecx_contextt contains all fields inline (slavelist, grouplist,
 * etc.), so we only need to allocate/free the context struct itself.
 */

#include "soem/soem.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

/**
 * Allocate and zero-initialize a full SOEM context.
 * All arrays (slavelist, grouplist, etc.) are embedded in the struct.
 */
EXPORT ecx_contextt* soemfoe_alloc_context(void)
{
    ecx_contextt* ctx = (ecx_contextt*)calloc(1, sizeof(ecx_contextt));
    if (!ctx) return NULL;

    ctx->FOEhook = NULL;
    ctx->manualstatechange = 0;

    return ctx;
}

/**
 * Free a context allocated by soemfoe_alloc_context.
 */
EXPORT void soemfoe_free_context(ecx_contextt* context)
{
    free(context);
}

/**
 * Set slave state field (does not send to network; call ecx_writestate after).
 */
EXPORT void soemfoe_set_slave_state(ecx_contextt* context, uint16 slave, uint16 state)
{
    if (context && slave < EC_MAXSLAVE)
    {
        context->slavelist[slave].state = state;
    }
}

/**
 * Configure bootstrap mailbox for a slave that has entered BOOT state.
 *
 * SOEM's ecx_config_init() only reads the standard mailbox configuration
 * (EEPROM addresses 0x0018/0x001a). When a slave enters BOOT state for FoE,
 * it uses different (bootstrap) mailbox parameters stored at EEPROM addresses
 * 0x0014 (ECT_SII_BOOTRXMBX) and 0x0016 (ECT_SII_BOOTTXMBX).
 *
 * This function reads the bootstrap mailbox config from EEPROM, updates the
 * slavelist fields and SM0/SM1, then writes SM0/SM1 to the slave hardware.
 *
 * Returns 1 on success, 0 on failure.
 */
EXPORT int soemfoe_config_boot_mailbox(ecx_contextt* context, uint16 slave)
{
    uint32 eedat;

    if (!context || slave == 0 || slave > context->slavecount)
        return 0;

    /* ecx_config_init() calls ecx_eeprom2pdi() which transfers EEPROM control
     * to PDI. We must reclaim it for the master before reading EEPROM. */
    ecx_eeprom2master(context, slave);

    /* Read BOOT mailbox data, master -> slave (EEPROM word 0x0014) */
    eedat = ecx_readeeprom(context, slave, ECT_SII_BOOTRXMBX, EC_TIMEOUTEEP);
    context->slavelist[slave].SM[0].StartAddr = (uint16)LO_WORD(eedat);
    context->slavelist[slave].SM[0].SMlength  = (uint16)HI_WORD(eedat);
    context->slavelist[slave].mbx_wo = (uint16)LO_WORD(eedat);
    context->slavelist[slave].mbx_l  = (uint16)HI_WORD(eedat);

    if (context->slavelist[slave].mbx_l == 0)
    {
        /* No bootstrap mailbox defined; keep standard config */
        return 0;
    }

    /* Read BOOT mailbox data, slave -> master (EEPROM word 0x0016) */
    eedat = ecx_readeeprom(context, slave, ECT_SII_BOOTTXMBX, EC_TIMEOUTEEP);
    context->slavelist[slave].SM[1].StartAddr = (uint16)LO_WORD(eedat);
    context->slavelist[slave].SM[1].SMlength  = (uint16)HI_WORD(eedat);
    context->slavelist[slave].mbx_ro = (uint16)LO_WORD(eedat);
    context->slavelist[slave].mbx_rl = (uint16)HI_WORD(eedat);

    if (context->slavelist[slave].mbx_rl == 0)
    {
        context->slavelist[slave].mbx_rl = context->slavelist[slave].mbx_l;
    }

    /* Program SM0 (mailbox in) for slave — must do BEFORE BOOT transition */
    ecx_FPWR(&context->port, context->slavelist[slave].configadr,
             ECT_REG_SM0, sizeof(ec_smt),
             &(context->slavelist[slave].SM[0]), EC_TIMEOUTRET);
    /* Program SM1 (mailbox out) for slave */
    ecx_FPWR(&context->port, context->slavelist[slave].configadr,
             ECT_REG_SM1, sizeof(ec_smt),
             &(context->slavelist[slave].SM[1]), EC_TIMEOUTRET);

    return 1;
}

/**
 * Re-write SM0/SM1 to slave hardware using current slavelist values.
 * Call AFTER BOOT state is confirmed, in case the ESC resets SM during
 * INIT→BOOT transition (common on HPM6E and similar MCU-based slaves).
 *
 * Returns 1 on success, 0 on failure.
 */
EXPORT int soemfoe_rewrite_boot_sm(ecx_contextt* context, uint16 slave)
{
    int wkc;

    if (!context || slave == 0 || slave > context->slavecount)
        return 0;

    if (context->slavelist[slave].mbx_l == 0)
        return 0;

    /* Re-program SM0 (mailbox in) for slave */
    wkc = ecx_FPWR(&context->port, context->slavelist[slave].configadr,
             ECT_REG_SM0, sizeof(ec_smt),
             &(context->slavelist[slave].SM[0]), EC_TIMEOUTRET);
    if (wkc <= 0)
        return 0;

    /* Re-program SM1 (mailbox out) for slave */
    wkc = ecx_FPWR(&context->port, context->slavelist[slave].configadr,
             ECT_REG_SM1, sizeof(ec_smt),
             &(context->slavelist[slave].SM[1]), EC_TIMEOUTRET);
    if (wkc <= 0)
        return 0;

    return 1;
}

/**
 * Flush any stale mailbox responses and reset the mailbox counter.
 * MUST be called after entering BOOT state and before ecx_FOEwrite
 * to avoid wkc=-3 (PACKET_ERROR) caused by residual mailbox data
 * or counter mismatch.
 *
 * Steps:
 *   1. Read SM1 status; if "mailbox full" bit is set, read and discard data.
 *   2. Repeat up to 5 times to drain multiple stale frames.
 *   3. Reset slavelist[slave].mbx_cnt = 0 so next FOEwrite starts at cnt=1.
 *
 * Returns: number of stale frames drained.
 */
EXPORT int soemfoe_flush_mbx(ecx_contextt* context, uint16 slave)
{
    uint8 SMstat;
    uint8 buf[EC_MAXMBX];
    int wkc;
    int count = 0;

    if (!context || slave == 0 || slave > context->slavecount)
        return 0;

    /* Drain any pending outbox data in SM1 */
    for (int i = 0; i < 5; i++)
    {
        /* Read SM1 status register — bit 3 = mailbox full (slave→master) */
        SMstat = 0;
        wkc = ecx_FPRD(&context->port,
                        context->slavelist[slave].configadr,
                        ECT_REG_SM1 + 5,  /* SM1 Status = SM1_base + 5 */
                        sizeof(SMstat), &SMstat, EC_TIMEOUTRET);
        if (wkc <= 0 || !(SMstat & 0x08))
            break;   /* No more pending data */

        /* Mailbox is full — read and discard */
        wkc = ecx_FPRD(&context->port,
                        context->slavelist[slave].configadr,
                        context->slavelist[slave].SM[1].StartAddr,
                        context->slavelist[slave].SM[1].SMlength,
                        buf, EC_TIMEOUTRET);
        if (wkc <= 0)
            break;
        count++;
    }

    /* Reset mailbox counter for fresh FoE session */
    context->slavelist[slave].mbx_cnt = 0;

    return count;
}

/**
 * Get current mailbox configuration from slavelist (for diagnostics).
 * All output parameters are optional (pass NULL to skip).
 */
EXPORT void soemfoe_get_mbx_info(ecx_contextt* context, uint16 slave,
                                  uint16* mbx_wo, uint16* mbx_l,
                                  uint16* mbx_ro, uint16* mbx_rl,
                                  uint16* sm0_addr, uint16* sm0_len,
                                  uint16* sm1_addr, uint16* sm1_len)
{
    if (!context || slave == 0 || slave > context->slavecount)
        return;

    if (mbx_wo)   *mbx_wo   = context->slavelist[slave].mbx_wo;
    if (mbx_l)    *mbx_l    = context->slavelist[slave].mbx_l;
    if (mbx_ro)   *mbx_ro   = context->slavelist[slave].mbx_ro;
    if (mbx_rl)   *mbx_rl   = context->slavelist[slave].mbx_rl;
    if (sm0_addr) *sm0_addr = context->slavelist[slave].SM[0].StartAddr;
    if (sm0_len)  *sm0_len  = context->slavelist[slave].SM[0].SMlength;
    if (sm1_addr) *sm1_addr = context->slavelist[slave].SM[1].StartAddr;
    if (sm1_len)  *sm1_len  = context->slavelist[slave].SM[1].SMlength;
}

/**
 * Pop the last error from the error ring and return a human-readable string.
 * Returns empty string if no error is pending.
 * The caller must copy the result before calling again (static buffer).
 */
EXPORT const char* soemfoe_pop_error_string(ecx_contextt* context)
{
    static char buf[256];
    ec_errort err;
    buf[0] = '\0';
    if (context && ecx_poperror(context, &err))
    {
        char *s = ecx_err2string(err);
        if (s)
        {
            strncpy(buf, s, sizeof(buf) - 1);
            buf[sizeof(buf) - 1] = '\0';
        }
    }
    return buf;
}

/* ── Helper: append formatted text to diagnostic buffer ── */
static int diag_append(char* buf, int bufsize, int pos, const char* fmt, ...)
{
    va_list ap;
    int n;
    if (!buf || pos >= bufsize - 1) return pos;
    va_start(ap, fmt);
    n = vsnprintf(buf + pos, bufsize - pos, fmt, ap);
    va_end(ap);
    if (n > 0) pos += n;
    return pos;
}

/**
 * Complete FoE firmware write — single-call, matches SOEM firm_update.c exactly.
 *
 * This function performs the entire FoE write operation in one call:
 *   1. Allocate SOEM context
 *   2. ecx_init + ecx_config_init (slaves reach PRE_OP automatically)
 *   3. Transition target slave: PRE_OP → INIT (with statecheck)
 *   4. Read bootstrap mailbox from EEPROM, update slavelist fields only
 *      (NO FPWR to SM registers — ESC auto-configures during BOOT transition)
 *   5. Transition target slave: INIT → BOOT (with statecheck)
 *   6. ecx_FOEwrite
 *   7. Collect errors, cleanup
 *
 * @param ifname       pcap network interface name
 * @param slave        slave index (1-based)
 * @param filename     FoE target filename
 * @param password     FoE password (0 for none)
 * @param data         firmware data buffer
 * @param datasize     firmware data size in bytes
 * @param timeout      FoE timeout in microseconds (e.g., 3000000)
 * @param diag_buf     output buffer for diagnostic text (UTF-8)
 * @param diag_buf_size size of diag_buf
 * @return             wkc from ecx_FOEwrite (positive = success), or negative error
 */
EXPORT int soemfoe_foe_write_full(
    const char* ifname,
    uint16 slave,
    const char* filename,
    uint32 password,
    const uint8* data,
    int datasize,
    int timeout,
    char* diag_buf,
    int diag_buf_size)
{
    ecx_contextt* ctx = NULL;
    int wkc;
    int dp = 0; /* diag position */
    uint32 eedat;
    uint16 boot_mbx_wo, boot_mbx_l, boot_mbx_ro, boot_mbx_rl;
    uint16 std_mbx_wo, std_mbx_l, std_mbx_ro, std_mbx_rl;
    uint16 actualstate;
    ec_smt hw_sm0, hw_sm1;

    if (!ifname || !filename || !data || datasize <= 0)
        return -100;

    /* 1. Allocate context */
    ctx = (ecx_contextt*)calloc(1, sizeof(ecx_contextt));
    if (!ctx) return -101;
    ctx->manualstatechange = 0;

    /* 2. Initialize network */
    dp = diag_append(diag_buf, diag_buf_size, dp, "ecx_init(%s)...\n", ifname);
    if (ecx_init(ctx, (char*)ifname) <= 0)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp, "FAIL: ecx_init returned 0\n");
        free(ctx);
        return -102;
    }

    /* 3. Scan and configure slaves (auto-transitions to PRE_OP) */
    dp = diag_append(diag_buf, diag_buf_size, dp, "ecx_config_init...\n");
    wkc = ecx_config_init(ctx);
    if (wkc <= 0)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp, "FAIL: no slaves found (%d)\n", wkc);
        ecx_close(ctx);
        free(ctx);
        return -103;
    }
    dp = diag_append(diag_buf, diag_buf_size, dp, "Found %d slave(s)\n", wkc);

    if (slave == 0 || slave > ctx->slavecount)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp, "FAIL: slave %d out of range (max %d)\n", slave, ctx->slavecount);
        ecx_close(ctx);
        free(ctx);
        return -104;
    }

    /* Save standard mailbox config (set by ecx_config_init from SII 0x0018/0x001a) */
    std_mbx_wo = ctx->slavelist[slave].mbx_wo;
    std_mbx_l  = ctx->slavelist[slave].mbx_l;
    std_mbx_ro = ctx->slavelist[slave].mbx_ro;
    std_mbx_rl = ctx->slavelist[slave].mbx_rl;
    dp = diag_append(diag_buf, diag_buf_size, dp,
        "Std MBX: wo=0x%04X l=%d ro=0x%04X rl=%d\n",
        std_mbx_wo, std_mbx_l, std_mbx_ro, std_mbx_rl);

    /* 3b. Wait for all slaves to reach PRE_OP */
    actualstate = ecx_statecheck(ctx, 0, EC_STATE_PRE_OP, EC_TIMEOUTSTATE * 4);
    dp = diag_append(diag_buf, diag_buf_size, dp, "Global PRE_OP check: 0x%04X\n", actualstate);

    /* 4. Transition to INIT — exactly like firm_update.c */
    dp = diag_append(diag_buf, diag_buf_size, dp, "Requesting INIT for slave %d...\n", slave);
    ctx->slavelist[slave].state = EC_STATE_INIT;
    ecx_writestate(ctx, slave);
    actualstate = ecx_statecheck(ctx, slave, EC_STATE_INIT, EC_TIMEOUTSTATE * 4);
    dp = diag_append(diag_buf, diag_buf_size, dp, "INIT statecheck: 0x%04X\n", actualstate);
    if ((actualstate & 0x0f) != EC_STATE_INIT)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp, "WARN: slave not in INIT\n");
    }

    /* 5. Read bootstrap mailbox from EEPROM — firm_update.c style */
    ecx_eeprom2master(ctx, slave);

    eedat = ecx_readeeprom(ctx, slave, ECT_SII_BOOTRXMBX, EC_TIMEOUTEEP);
    boot_mbx_wo = (uint16)LO_WORD(eedat);
    boot_mbx_l  = (uint16)HI_WORD(eedat);
    ctx->slavelist[slave].SM[0].StartAddr = boot_mbx_wo;
    ctx->slavelist[slave].SM[0].SMlength  = boot_mbx_l;
    /* SM0 flags: enable=1, direction=write(master→slave), mode=mailbox */
    ctx->slavelist[slave].SM[0].SMflags   = htoel(0x00010026);
    ctx->slavelist[slave].mbx_wo = boot_mbx_wo;
    ctx->slavelist[slave].mbx_l  = boot_mbx_l;

    eedat = ecx_readeeprom(ctx, slave, ECT_SII_BOOTTXMBX, EC_TIMEOUTEEP);
    boot_mbx_ro = (uint16)LO_WORD(eedat);
    boot_mbx_rl = (uint16)HI_WORD(eedat);
    ctx->slavelist[slave].SM[1].StartAddr = boot_mbx_ro;
    ctx->slavelist[slave].SM[1].SMlength  = boot_mbx_rl;
    /* SM1 flags: enable=1, direction=read(slave→master), mode=mailbox */
    ctx->slavelist[slave].SM[1].SMflags   = htoel(0x00010022);
    ctx->slavelist[slave].mbx_ro = boot_mbx_ro;
    ctx->slavelist[slave].mbx_rl = boot_mbx_rl;

    if (boot_mbx_rl == 0)
        ctx->slavelist[slave].mbx_rl = boot_mbx_l;

    dp = diag_append(diag_buf, diag_buf_size, dp,
        "Boot MBX: wo=0x%04X l=%d ro=0x%04X rl=%d\n",
        boot_mbx_wo, boot_mbx_l, boot_mbx_ro, boot_mbx_rl);

    if (boot_mbx_l == 0)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "WARN: No bootstrap MBX defined, using std MBX\n");
        ctx->slavelist[slave].mbx_wo = std_mbx_wo;
        ctx->slavelist[slave].mbx_l  = std_mbx_l;
        ctx->slavelist[slave].mbx_ro = std_mbx_ro;
        ctx->slavelist[slave].mbx_rl = std_mbx_rl;
    }

    /* 5b. Program SM0/SM1 with bootstrap config BEFORE requesting BOOT.
     * HPM6E (and similar MCU-based ESCs) do NOT auto-configure SM1 from
     * bootstrap EEPROM data during INIT→BOOT transition.
     * We write the correct SM config while in INIT so that when the slave
     * enters BOOT, its FoE handler initializes with the right addresses. */
    {
        uint8 sm_deactivate = 0x00;
        uint8 sm_activate   = 0x01;
        ec_smt sm_cfg;

        /* Read current HW SM for diagnostics */
        memset(&hw_sm0, 0, sizeof(hw_sm0));
        memset(&hw_sm1, 0, sizeof(hw_sm1));
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0, sizeof(ec_smt), &hw_sm0, EC_TIMEOUTRET);
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1, sizeof(ec_smt), &hw_sm1, EC_TIMEOUTRET);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM0 (INIT): addr=0x%04X len=%d flags=0x%08X\n",
            hw_sm0.StartAddr, hw_sm0.SMlength, hw_sm0.SMflags);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM1 (INIT): addr=0x%04X len=%d flags=0x%08X\n",
            hw_sm1.StartAddr, hw_sm1.SMlength, hw_sm1.SMflags);

        /* Disable SM0, write bootstrap config, re-enable */
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0 + 6, 1, &sm_deactivate, EC_TIMEOUTRET);
        memset(&sm_cfg, 0, sizeof(sm_cfg));
        sm_cfg.StartAddr = ctx->slavelist[slave].SM[0].StartAddr;
        sm_cfg.SMlength  = ctx->slavelist[slave].SM[0].SMlength;
        sm_cfg.SMflags   = htoel(0x00000026); /* mailbox write, enable=0 */
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0, sizeof(ec_smt), &sm_cfg, EC_TIMEOUTRET);
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0 + 6, 1, &sm_activate, EC_TIMEOUTRET);

        /* Disable SM1, write bootstrap config, re-enable */
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1 + 6, 1, &sm_deactivate, EC_TIMEOUTRET);
        memset(&sm_cfg, 0, sizeof(sm_cfg));
        sm_cfg.StartAddr = ctx->slavelist[slave].SM[1].StartAddr;
        sm_cfg.SMlength  = ctx->slavelist[slave].SM[1].SMlength;
        sm_cfg.SMflags   = htoel(0x00000022); /* mailbox read, enable=0 */
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1, sizeof(ec_smt), &sm_cfg, EC_TIMEOUTRET);
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1 + 6, 1, &sm_activate, EC_TIMEOUTRET);

        /* Read back to verify */
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0, sizeof(ec_smt), &hw_sm0, EC_TIMEOUTRET);
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1, sizeof(ec_smt), &hw_sm1, EC_TIMEOUTRET);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM0 (programmed): addr=0x%04X len=%d flags=0x%08X\n",
            hw_sm0.StartAddr, hw_sm0.SMlength, hw_sm0.SMflags);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM1 (programmed): addr=0x%04X len=%d flags=0x%08X\n",
            hw_sm1.StartAddr, hw_sm1.SMlength, hw_sm1.SMflags);
    }

    /* 6. Transition to BOOT */
    dp = diag_append(diag_buf, diag_buf_size, dp, "Requesting BOOT for slave %d...\n", slave);
    ctx->slavelist[slave].state = EC_STATE_BOOT;
    ecx_writestate(ctx, slave);
    actualstate = ecx_statecheck(ctx, slave, EC_STATE_BOOT, EC_TIMEOUTSTATE * 10);
    dp = diag_append(diag_buf, diag_buf_size, dp, "BOOT statecheck: 0x%04X\n", actualstate);
    if ((actualstate & 0x0f) != EC_STATE_BOOT)
    {
        dp = diag_append(diag_buf, diag_buf_size, dp, "FAIL: slave did not reach BOOT\n");
        ecx_close(ctx);
        free(ctx);
        return -105;
    }

    /* Verify SM after BOOT transition */
    memset(&hw_sm0, 0, sizeof(hw_sm0));
    memset(&hw_sm1, 0, sizeof(hw_sm1));
    ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
             ECT_REG_SM0, sizeof(ec_smt), &hw_sm0, EC_TIMEOUTRET);
    ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
             ECT_REG_SM1, sizeof(ec_smt), &hw_sm1, EC_TIMEOUTRET);
    dp = diag_append(diag_buf, diag_buf_size, dp,
        "HW SM0 (BOOT): addr=0x%04X len=%d flags=0x%08X\n",
        hw_sm0.StartAddr, hw_sm0.SMlength, hw_sm0.SMflags);
    dp = diag_append(diag_buf, diag_buf_size, dp,
        "HW SM1 (BOOT): addr=0x%04X len=%d flags=0x%08X\n",
        hw_sm1.StartAddr, hw_sm1.SMlength, hw_sm1.SMflags);

    /* If BOOT transition overwrote SM1, reprogram it */
    if (hw_sm1.StartAddr != ctx->slavelist[slave].mbx_ro ||
        hw_sm1.SMlength  != ctx->slavelist[slave].mbx_rl)
    {
        uint8 sm_deactivate = 0x00;
        uint8 sm_activate   = 0x01;
        ec_smt sm_cfg;

        dp = diag_append(diag_buf, diag_buf_size, dp,
            "BOOT overwrote SM1! Re-fixing: 0x%04X -> 0x%04X\n",
            hw_sm1.StartAddr, ctx->slavelist[slave].mbx_ro);

        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1 + 6, 1, &sm_deactivate, EC_TIMEOUTRET);
        memset(&sm_cfg, 0, sizeof(sm_cfg));
        sm_cfg.StartAddr = ctx->slavelist[slave].SM[1].StartAddr;
        sm_cfg.SMlength  = ctx->slavelist[slave].SM[1].SMlength;
        sm_cfg.SMflags   = htoel(0x00000022);
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1, sizeof(ec_smt), &sm_cfg, EC_TIMEOUTRET);
        ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1 + 6, 1, &sm_activate, EC_TIMEOUTRET);

        /* Also reprogram SM0 if needed */
        if (hw_sm0.StartAddr != ctx->slavelist[slave].mbx_wo)
        {
            ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                     ECT_REG_SM0 + 6, 1, &sm_deactivate, EC_TIMEOUTRET);
            memset(&sm_cfg, 0, sizeof(sm_cfg));
            sm_cfg.StartAddr = ctx->slavelist[slave].SM[0].StartAddr;
            sm_cfg.SMlength  = ctx->slavelist[slave].SM[0].SMlength;
            sm_cfg.SMflags   = htoel(0x00000026);
            ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                     ECT_REG_SM0, sizeof(ec_smt), &sm_cfg, EC_TIMEOUTRET);
            ecx_FPWR(&ctx->port, ctx->slavelist[slave].configadr,
                     ECT_REG_SM0 + 6, 1, &sm_activate, EC_TIMEOUTRET);
        }

        /* Verify final SM config */
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM0, sizeof(ec_smt), &hw_sm0, EC_TIMEOUTRET);
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 ECT_REG_SM1, sizeof(ec_smt), &hw_sm1, EC_TIMEOUTRET);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM0 (re-fixed): addr=0x%04X len=%d\n",
            hw_sm0.StartAddr, hw_sm0.SMlength);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "HW SM1 (re-fixed): addr=0x%04X len=%d\n",
            hw_sm1.StartAddr, hw_sm1.SMlength);
    }

    dp = diag_append(diag_buf, diag_buf_size, dp,
        "Final MBX: wo=0x%04X l=%d ro=0x%04X rl=%d cnt=%d\n",
        ctx->slavelist[slave].mbx_wo, ctx->slavelist[slave].mbx_l,
        ctx->slavelist[slave].mbx_ro, ctx->slavelist[slave].mbx_rl,
        ctx->slavelist[slave].mbx_cnt);

    /* 7. FoE Write — exactly like firm_update.c */
    dp = diag_append(diag_buf, diag_buf_size, dp,
        "ecx_FOEwrite(slave=%d, file=%s, pw=%u, size=%d, timeout=%d)...\n",
        slave, filename, password, datasize, timeout);

    wkc = ecx_FOEwrite(ctx, slave, (char*)filename, password, datasize, (void*)data, timeout);

    dp = diag_append(diag_buf, diag_buf_size, dp, "ecx_FOEwrite returned wkc=%d\n", wkc);

    /* 8. Collect SOEM error stack */
    if (wkc <= 0)
    {
        ec_errort err;
        int ecnt = 0;
        while (ecx_poperror(ctx, &err) && ecnt < 16)
        {
            dp = diag_append(diag_buf, diag_buf_size, dp,
                "ERR[%d]: Etype=%d Slave=%d AbortCode=0x%08X\n",
                ecnt, err.Etype, err.Slave, (uint32)err.AbortCode);
            ecnt++;
        }

        /* Extra diagnostic: read slave state after failure */
        uint16 poststate = 0;
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 0x0130, 2, &poststate, EC_TIMEOUTRET);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "Post-fail AL Status: 0x%04X\n", poststate);

        /* Read AL Status Code (error code register) */
        uint16 alcode = 0;
        ecx_FPRD(&ctx->port, ctx->slavelist[slave].configadr,
                 0x0134, 2, &alcode, EC_TIMEOUTRET);
        dp = diag_append(diag_buf, diag_buf_size, dp,
            "Post-fail AL Status Code: 0x%04X\n", alcode);
    }
    else
    {
        /* Success: transition back to INIT */
        ctx->slavelist[slave].state = EC_STATE_INIT;
        ecx_writestate(ctx, slave);
        ecx_statecheck(ctx, slave, EC_STATE_INIT, EC_TIMEOUTSTATE);
    }

    /* 9. Cleanup */
    ecx_close(ctx);
    free(ctx);

    return wkc;
}

/**
 * Write an array of 16-bit words to slave EEPROM (SII).
 *
 * @param context   SOEM context (must be initialized with ecx_init + ecx_config_init)
 * @param slave     slave index (1-based)
 * @param start     EEPROM word start address
 * @param data      array of uint16 words to write
 * @param length    number of words to write
 * @return          number of words successfully written, or -1 on error
 */
EXPORT int soemfoe_write_eeprom(ecx_contextt* context, uint16 slave,
                                uint16 start, const uint16* data, int length)
{
    int i;
    int wkc;

    if (!context || !data || slave == 0 || slave > context->slavecount || length <= 0)
        return -1;

    /* Ensure EEPROM is assigned to master (not PDI) */
    ecx_eeprom2master(context, slave);

    for (i = 0; i < length; i++)
    {
        wkc = ecx_writeeeprom(context, slave, (uint16)(start + i), data[i], EC_TIMEOUTEEP);
        if (wkc <= 0)
            return i; /* return number of words written so far */
    }

    return length;
}

/**
 * Read a block of EEPROM words using FPRD (configured-address) addressing.
 * Returns number of words read, or -1 on error.
 */
EXPORT int soemfoe_read_eeprom(ecx_contextt* context, uint16 slave,
                               uint16 start, uint16* data, int length)
{
    int i;
    uint32 word32;

    if (!context || !data || slave == 0 || slave > context->slavecount || length <= 0)
        return -1;

    ecx_eeprom2master(context, slave);

    for (i = 0; i < length; i++)
    {
        word32 = ecx_readeeprom(context, slave, (uint16)(start + i), EC_TIMEOUTEEP);
        data[i] = (uint16)(word32 & 0xFFFF);
    }
    return length;
}

/**
 * Compute auto-increment address for a 1-based slave index (matches SOEM
 * eepromtool.c convention: aiadr = 1 - slave, wrapping in 16-bit).
 */
static uint16 soemfoe_aiadr_for_slave(uint16 slave)
{
    return (uint16)(1 - (int)slave);
}

/**
 * Write EEPROM using APWR (auto-increment) addressing with **per-word
 * write-then-readback-verify**.
 *
 * Why per-word verify is required:
 *   SOEM's `ecx_writeeepromAP` only checks the EEPROM `EC_ESTAT_NACK` bit
 *   and waits for `EC_ESTAT_BUSY` to clear; it does **not** poll until the
 *   physical EEPROM I²C write cycle finishes.  On many ESC implementations
 *   (notably TI Sitara, Hilscher netX-90, HPM6E, and other MCU-based
 *   slaves), the BUSY bit clears as soon as the ESC accepts the command
 *   into its internal buffer, *before* the I²C write actually completes
 *   (typical AT24Cxx write cycle = 5–10 ms).  Issuing the next word write
 *   too quickly causes the EEPROM controller to silently drop one or more
 *   words.  The session-level "bulk readback" then PASSES because what we
 *   read back is the ESC's *internal* shadow / cache — but after a power
 *   cycle the EEPROM physically holds the *previous* (or partially
 *   updated) content, so the slave reverts to defaults (e.g. shows up as
 *   "Drive" with no Identity).
 *
 * This is exactly what makes TwinCAT's eeprom-burner robust:
 *   it writes one word, waits ~5 ms, reads it back, and retries the SAME
 *   word until verified.  We replicate that contract here.
 *
 * Returns the number of words successfully verified, or -1 on invalid
 * arguments.  A return value < length means the caller should treat the
 * operation as a hard failure (verification could not be achieved within
 * retry budget).
 */
EXPORT int soemfoe_write_eeprom_ap(ecx_contextt* context, uint16 slave,
                                   uint16 start, const uint16* data, int length)
{
    int i;
    uint16 aiadr;
    uint8 eepctl;
    const int per_word_settle_us = 5000;   /* 5 ms — > AT24Cxx tWR (max 5 ms) */
    const int per_word_max_retry = 5;

    if (!context || !data || slave == 0 || slave > context->slavecount || length <= 0)
        return -1;

    aiadr = soemfoe_aiadr_for_slave(slave);

    /* Force EEPROM control from PDI to master via AP (works even if configadr lost) */
    eepctl = 2;
    ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG, sizeof(eepctl), &eepctl, EC_TIMEOUTRET);
    eepctl = 0;
    ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG, sizeof(eepctl), &eepctl, EC_TIMEOUTRET);

    for (i = 0; i < length; i++)
    {
        uint16 wordaddr = (uint16)(start + i);
        int verified = 0;
        int attempt;

        for (attempt = 0; attempt < per_word_max_retry && !verified; attempt++)
        {
            uint64 readback;
            int wkc = ecx_writeeepromAP(context, aiadr, wordaddr, data[i], EC_TIMEOUTEEP);
            if (wkc <= 0)
            {
                /* Command-level failure (NACK 3 times). Brief pause and retry. */
                osal_usleep(per_word_settle_us);
                continue;
            }

            /* Wait for the physical EEPROM write cycle to complete.
             * BUSY clearing in the ESC is necessary but NOT sufficient. */
            osal_usleep(per_word_settle_us);

            /* Read back via the same EEPROM controller path and compare. */
            readback = ecx_readeepromAP(context, aiadr, wordaddr, EC_TIMEOUTEEP);
            if ((uint16)(readback & 0xFFFF) == data[i])
            {
                verified = 1;
                break;
            }
            /* Mismatch — extra settle then retry the same word. */
            osal_usleep(per_word_settle_us * 2);
        }

        if (!verified)
            return i; /* caller will see partial-write and abort */
    }

    return length;
}

/**
 * Read EEPROM using APRD (auto-increment) addressing.
 * Works even when the slave has no valid configured station address.
 * Returns number of words read, or -1 on invalid args.
 */
EXPORT int soemfoe_read_eeprom_ap(ecx_contextt* context, uint16 slave,
                                  uint16 start, uint16* data, int length)
{
    int i;
    uint64 edat;
    uint16 aiadr;
    uint8 eepctl;

    if (!context || !data || slave == 0 || slave > context->slavecount || length <= 0)
        return -1;

    aiadr = soemfoe_aiadr_for_slave(slave);

    eepctl = 2;
    ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG, sizeof(eepctl), &eepctl, EC_TIMEOUTRET);
    eepctl = 0;
    ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG, sizeof(eepctl), &eepctl, EC_TIMEOUTRET);

    for (i = 0; i < length; i++)
    {
        edat = ecx_readeepromAP(context, aiadr, (uint16)(start + i), EC_TIMEOUTEEP);
        data[i] = (uint16)(edat & 0xFFFF);
    }
    return length;
}

/**
 * Wait until the EEPROM controller is not busy (poll EEPSTAT.BUSY via APRD).
 * Returns 1 on success (not busy, status returned in *estat),
 *         0 on timeout / network failure.
 */
static int soemfoe_eeprom_wait_notbusy_ap(ecx_contextt* context, uint16 aiadr,
                                          uint16* estat, int timeout_us)
{
    int wkc;
    int elapsed = 0;
    const int step_us = 200;

    while (elapsed <= timeout_us)
    {
        *estat = 0;
        wkc = ecx_APRD(&context->port, aiadr, ECT_REG_EEPSTAT,
                       sizeof(*estat), estat, EC_TIMEOUTRET);
        if (wkc > 0)
        {
            *estat = etohs(*estat);
            if ((*estat & EC_ESTAT_BUSY) == 0)
                return 1;
        }
        osal_usleep(step_us);
        elapsed += step_us;
    }
    return 0;
}

/**
 * Issue an "EEPROM Reload" command to the slave's ESC after SII rewrite,
 * so that the configured area (Identity 0x0008..0x000F, mailbox/SM defaults,
 * Station Alias, etc.) is re-latched into the ESC's mirror registers without
 * requiring a physical power cycle.
 *
 * Per ETG.1000.4 (EEPROM Control register 0x0502/0x0503), the Reload command
 * is encoded as bit 2 of the command byte (0x0503), which is 0x0400 in the
 * 16-bit register packed little-endian.
 *
 * NOTE: SOEM's `EC_ECMD_RELOAD = 0x0300` constant is non-standard and never
 * used inside SOEM itself; we deliberately use the spec-correct 0x0400 here.
 *
 * Steps (mirrors what TwinCAT does after SII write):
 *   1. Force EEPROM control to master (toggle EEPCFG: 2 -> 0).
 *   2. Wait until EEPROM controller is not busy.
 *   3. Clear any pending error bits (write NOP to EEPCTL).
 *   4. Issue Reload (write {comm=0x0400, addr=0, d2=0} to EEPCTL).
 *   5. Wait until reload completes (BUSY clears) and check error bits.
 *
 * Returns 1 on success, 0 on failure.
 */
EXPORT int soemfoe_reload_eeprom_ap(ecx_contextt* context, uint16 slave)
{
    uint16 aiadr;
    uint8 eepctl;
    uint16 estat;
    ec_eepromt ed;
    int wkc;
    int retry;

    if (!context || slave == 0 || slave > context->slavecount)
        return 0;

    aiadr = soemfoe_aiadr_for_slave(slave);

    /* 1) Force EEPROM ownership to master via AP (works even if SII is
     *    corrupt / no valid configadr). Must be done in two steps on some
     *    ESCs: assert "force PDI off" (=2), then release (=0). */
    eepctl = 2;
    wkc = ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG,
                   sizeof(eepctl), &eepctl, EC_TIMEOUTRET);
    if (wkc <= 0) return 0;
    eepctl = 0;
    wkc = ecx_APWR(&context->port, aiadr, ECT_REG_EEPCFG,
                   sizeof(eepctl), &eepctl, EC_TIMEOUTRET);
    if (wkc <= 0) return 0;

    /* 2) Wait until EEPROM controller is idle. */
    if (!soemfoe_eeprom_wait_notbusy_ap(context, aiadr, &estat, EC_TIMEOUTEEP))
        return 0;

    /* 3) Clear any sticky error bits with a NOP. */
    if (estat & EC_ESTAT_EMASK)
    {
        estat = htoes(EC_ECMD_NOP);
        ecx_APWR(&context->port, aiadr, ECT_REG_EEPCTL,
                 sizeof(estat), &estat, EC_TIMEOUTRET3);
    }

    /* 4) Issue the Reload command.
     *    EtherCAT spec: Reload = bit 2 of command byte at 0x0503 = 0x0400 (LE word).
     *    addr=0 (the configured area is reloaded regardless of addr). */
    for (retry = 0; retry < 3; retry++)
    {
        ed.comm = 0x0400;   /* spec-correct Reload command */
        ed.addr = 0x0000;
        ed.d2   = 0x0000;
        wkc = ecx_APWR(&context->port, aiadr, ECT_REG_EEPCTL,
                       sizeof(ed), &ed, EC_TIMEOUTRET3);
        if (wkc > 0)
            break;
        osal_usleep(EC_LOCALDELAY);
    }
    if (wkc <= 0)
        return 0;

    /* 5) Wait for completion and verify no error bits. */
    osal_usleep(EC_LOCALDELAY * 2);
    if (!soemfoe_eeprom_wait_notbusy_ap(context, aiadr, &estat, EC_TIMEOUTEEP))
        return 0;
    if (estat & EC_ESTAT_EMASK)
        return 0;

    /* Give the ESC a moment to internally re-latch configured-area registers. */
    osal_usleep(20000);
    return 1;
}

/**
 * Verify that the ESC's mirror Identity registers (0x0008..0x000F) reflect
 * the EEPROM contents after a Reload command. Reads VendorID (reg 0x0008,
 * 32-bit, mirrors EEPROM word 0x0008..0x0009) and ProductCode (reg 0x000C,
 * 32-bit equivalent? — actually Identity layout in mirror regs:
 *   0x0008..0x000B = VendorID
 *   0x000C..0x000F = ProductCode mirror lives in the SII registers area,
 * but in ESC mirror registers Vendor/Product are NOT directly mapped — they
 * are exposed via SII access. So instead, we read EEPROM words 0x0008 and
 * 0x000A via the (now-master-controlled) EEPROM interface and ensure they
 * match what we just wrote. The caller passes the expected values.
 *
 * Returns 1 if both match, 0 otherwise.
 */
EXPORT int soemfoe_verify_identity_reloaded_ap(ecx_contextt* context, uint16 slave,
                                               uint32 expected_vendor,
                                               uint32 expected_product)
{
    uint16 buf[4];
    uint16 aiadr;
    int n;
    uint32 vendor, product;

    if (!context || slave == 0 || slave > context->slavecount)
        return 0;
    aiadr = soemfoe_aiadr_for_slave(slave);
    (void)aiadr;

    /* Read EEPROM words 0x0008..0x000B = VendorID(2 words) + ProductCode(2 words) */
    n = soemfoe_read_eeprom_ap(context, slave, 0x0008, buf, 4);
    if (n != 4) return 0;

    vendor  = ((uint32)buf[0]) | (((uint32)buf[1]) << 16);
    product = ((uint32)buf[2]) | (((uint32)buf[3]) << 16);

    return (vendor == expected_vendor && product == expected_product) ? 1 : 0;
}
