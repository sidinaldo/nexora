import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CanalWhatsapp, Conexao as ConexaoModel, Conexoes as ConexoesDto } from '../../nucleo/modelos';
import { Conexao } from './conexao';

/** MULTI-NÚMERO NA TELA (ARQ-2).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Três decisões que são fáceis de "melhorar" para pior:
 *
 *    1. quem decide se dá para APAGAR é o servidor (`podeRemover`/`motivoNaoRemove`) — só o banco
 *       sabe se há conversa apontando para a conexão. Recalcular aqui daria um botão habilitado
 *       que às vezes devolve erro;
 *    2. quem decide se dá para ADICIONAR é o servidor (`podeAdicionar`), porque o limite vem do
 *       contrato e muda sem a tela saber;
 *    3. o polling de 3s existe SÓ durante o pareamento. Voltar a poll contínuo agora custaria N
 *       requisições por tick, uma por número, e a Evolution responde uma por instância.
 *  ====================================================================== */
describe('conexão — multi-número', () => {
  function conexao(over: Partial<ConexaoModel> = {}): ConexaoModel {
    return {
      id: 1, nome: 'Principal', instanceName: 'emp-1', numero: '5584900000001',
      numeroAnterior: null, perfilNome: 'Padaria', perfilFotoUrl: null,
      status: 'conectado', conectadoEm: '2026-08-01T12:00:00Z', desconectadoEm: null,
      conversas: 0, podeRemover: true, motivoNaoRemove: null,
      canal: 'evolution', oficial: false, phoneNumberId: null, wabaId: null,
      tokenConfigurado: false, appSecretConfigurado: false, verifyToken: null, webhookVerificadoEm: null,
      ...over
    };
  }

  /** Uma conexão da API oficial (INT-XX), como o servidor a manda: sem token nem segredo. */
  function oficial(over: Partial<ConexaoModel> = {}): ConexaoModel {
    return conexao({
      id: 2, nome: 'Oficial', instanceName: 'cloud-1-2', canal: 'cloud_api', oficial: true,
      phoneNumberId: '1090000000001', wabaId: '2090000000001',
      tokenConfigurado: true, appSecretConfigurado: true, verifyToken: 'abc123', ...over
    });
  }

  let http: HttpTestingController;
  let fixture: ComponentFixture<Conexao>;
  let c: Conexao;

  /** Abre a tela: a lista do banco e, logo depois, a conferida na Evolution. `conferida` é a
   *  mesma por padrão — só os testes da conferência precisam que as duas difiram. */
  /** A resposta como o servidor a manda: `emUso` é contado lá (AUD-XX), e aqui o fixture o preenche
   *  com o tamanho da lista — que é o que o servidor faria com estes dados. */
  type SemUso = Omit<ConexoesDto, 'emUso' | 'canalPadrao'> & { emUso?: number; canalPadrao?: CanalWhatsapp };
  const comUso = (r: SemUso): ConexoesDto =>
    ({ ...r, emUso: r.emUso ?? r.itens.length, canalPadrao: r.canalPadrao ?? 'evolution' });

  function montar(resposta: SemUso, conferida: SemUso = resposta) {
    fixture = TestBed.createComponent(Conexao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET').flush(comUso(resposta));
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conexoes/conferir') && r.method === 'POST').flush(comUso(conferida));
    fixture.detectChanges();
  }

  /** O TEXTO renderizado, não o innerHTML: aspas viram `&quot;` só em atributo, e comparar
   *  markup faria o teste quebrar a cada mudança de classe. */
  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  function botoes(rotulo: string): HTMLButtonElement[] {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
      .filter(b => b.textContent?.trim().startsWith(rotulo)) as HTMLButtonElement[];
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => { TestBed.resetTestingModule(); });

  /** "N de M" usa o `emUso` do SERVIDOR (AUD-XX), e não o tamanho da lista. Aqui eles diferem de
   *  propósito: se a tela voltar a contar a lista, ela diz "1 de 3". */
  it('O "N DE M" DOS NÚMEROS É O DO SERVIDOR', () => {
    montar({ limite: 3, podeAdicionar: true, itens: [conexao()], emUso: 2 });
    expect(texto()).toContain('2 de 3');
  });

  // ==================================================================== apagar
  it('O BOTÃO APAGAR OBEDECE O SERVIDOR, NÃO UM CÁLCULO DA TELA', () => {
    montar({
      limite: 3,
      podeAdicionar: true,
      itens: [
        conexao({ id: 1, nome: 'Vendas', conversas: 42, podeRemover: false,
                  motivoNaoRemove: 'Este número tem 42 conversas no histórico.' }),
        conexao({ id: 2, nome: 'Suporte', numero: null, status: 'nao_criada' })
      ]
    });

    const apagar = botoes('Apagar');
    expect(apagar.length).toBe(2);

    // A com histórico: desabilitada, e o MOTIVO é o texto que veio do servidor. Sem ele o
    // usuário só descobre por que não pode depois de clicar.
    expect(apagar[0].disabled).withContext('a que tem histórico deveria estar travada').toBeTrue();
    expect(apagar[0].title).toBe('Este número tem 42 conversas no histórico.');

    // A limpa: liberada.
    expect(apagar[1].disabled).toBeFalse();
  });

  it('a última conexão vem travada pelo servidor e a tela não a libera', () => {
    montar({
      limite: 1,
      podeAdicionar: false,
      itens: [conexao({
        podeRemover: false,
        motivoNaoRemove: 'Esta é a única conexão da empresa. Sem ela nenhuma mensagem entra ou sai.'
      })]
    });

    expect(botoes('Apagar')[0].disabled).toBeTrue();
  });

  it('apagar passa pelo painel de confirmação e só então chama DELETE', () => {
    montar({ limite: 2, podeAdicionar: true, itens: [conexao({ id: 7, nome: 'Descartável' })] });

    c.pedirRemocao(c.lista()[0]);
    fixture.detectChanges();

    // Painel na PÁGINA, não `confirm()` do navegador: apagar leva a instância junto na Evolution,
    // e isso precisa caber na tela junto com a alternativa (desconectar).
    expect(texto()).toContain('Apagar "Descartável"');
    expect(texto()).toContain('desconecte');
    http.expectNone(() => true);

    c.confirmarRemocao();
    const req = http.expectOne(r => r.url.endsWith('/conexoes/7') && r.method === 'DELETE');
    req.flush(null);

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET')
      .flush({ limite: 2, podeAdicionar: true, itens: [] });
  });

  // ==================================================================== limite do plano
  it('SEM VAGA NO PLANO O FORMULÁRIO SOME E A TELA EXPLICA POR QUÊ', () => {
    montar({ limite: 1, podeAdicionar: false, itens: [conexao()] });

    expect(texto()).toContain('Seu plano permite 1 número');
    expect(botoes('Criar').length).withContext('não deveria haver formulário de novo número').toBe(0);
  });

  it('com vaga, criar manda POST e abre o pareamento do número novo', () => {
    montar({ limite: 2, podeAdicionar: true, itens: [conexao()] });

    c.fNome.set('Suporte');
    c.criar();

    const post = http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'POST');
    expect(post.request.body).toEqual({ nome: 'Suporte', canal: 'evolution' });
    post.flush({ id: 9 });

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET').flush({
      limite: 2, podeAdicionar: false,
      itens: [conexao(), conexao({ id: 9, nome: 'Suporte', numero: null, status: 'nao_criada' })]
    });

    // Criar sem conectar não serve para nada — o próximo passo é sempre o mesmo, e a tela já
    // abre nele.
    expect(c.abertaId()).toBe(9);
    http.expectOne(r => r.url.endsWith('/conexoes/9/saude')).flush(
      { enviadasHoje: 0, pendentes: 0, expiradas: 0, falhasHoje: 0 });
  });

  // ==================================================================== API oficial (INT-XX)
  it('cada número diz se é oficial ou não', () => {
    montar({ limite: 2, podeAdicionar: false, itens: [conexao(), oficial()] });

    const selos = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.selo-canal'))
      .map(e => e.textContent?.trim());
    expect(selos).toEqual(['Não oficial', 'Oficial']);
  });

  /** ⚠️ O AVISO DE SAIR DO APLICATIVO É CONFIRMADO, E NÃO SÓ MOSTRADO: sem o "entendi", nada vai
   *  para o servidor. E o formulário nasce no canal padrão da empresa. */
  it('criar oficial exige o aviso confirmado e manda as credenciais', () => {
    montar({ limite: 2, podeAdicionar: true, itens: [conexao()], canalPadrao: 'cloud_api' });
    expect(c.fCanal()).toBe('cloud_api');
    expect(texto()).toContain('não é migrado');

    c.fNome.set('Oficial');
    c.fPhoneNumberId.set('1090000000001');
    c.fWabaId.set('2090000000001');
    c.fToken.set('EAAG-tok');
    c.fAppSecret.set('seg');

    c.criar();
    http.expectNone(r => r.url.endsWith('/conexoes') && r.method === 'POST');
    expect(c.erroNovo()).toContain('aviso');

    c.fCiente.set(true);
    c.criar();
    const post = http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'POST');
    expect(post.request.body).toEqual({
      nome: 'Oficial', canal: 'cloud_api', phoneNumberId: '1090000000001', wabaId: '2090000000001',
      accessToken: 'EAAG-tok', appSecret: 'seg'
    });
  });

  it('a conexão oficial abre sem QR, com o webhook para copiar e o teste', () => {
    montar({ limite: 2, podeAdicionar: false, itens: [conexao(), oficial()] });

    c.abrir(c.lista()[1]);
    http.expectOne(r => r.url.endsWith('/conexoes/2/saude')).flush(
      { enviadasHoje: 0, pendentes: 0, expiradas: 0, falhasHoje: 0 });
    fixture.detectChanges();

    expect(texto()).toContain('Verify token');
    expect(texto()).toContain('abc123');
    expect(texto()).toContain('/webhook/meta');
    expect(texto()).not.toContain('Conectar com QR code');

    // Os templates (INT-XX) são do número oficial: a lista dele é pedida pelo id da conexão.
    http.expectOne(r => r.url.endsWith('/conexoes/2/modelos') && r.method === 'GET').flush([]);
    fixture.detectChanges();
    expect(texto()).toContain('Nenhum template ainda.');

    c.testar(2);
    http.expectOne(r => r.url.endsWith('/conexoes/2/testar') && r.method === 'POST').flush({
      ok: false, numero: '5584912345678', nomeVerificado: 'Loja', qualidade: 'GREEN',
      webhookVerificado: false, problemas: ['A Meta ainda não confirmou o webhook.']
    });
    fixture.detectChanges();
    expect(texto()).toContain('A Meta ainda não confirmou o webhook.');
  });

  // ==================================================================== renomear
  it('RENOMEAR MANDA SÓ O NOME — instanceName não tem rota de edição', () => {
    // ===================== A REGRA QUE NÃO PODE TER BOTÃO =====================
    // `instance_name` é a identidade na Evolution e a chave pela qual o webhook acha o tenant.
    // Editá-lo orfanaria a sessão e o sistema pararia de receber mensagem EM SILÊNCIO.
    // =========================================================================
    montar({ limite: 2, podeAdicionar: true, itens: [conexao({ id: 4, nome: 'Vendas' })] });

    c.editar(c.lista()[0]);
    c.eNome.set('Vendas Centro');
    c.salvarEdicao(c.lista()[0]);

    const put = http.expectOne(r => r.url.endsWith('/conexoes/4') && r.method === 'PUT');
    expect(put.request.body).toEqual({ nome: 'Vendas Centro' });
    expect(JSON.stringify(put.request.body)).not.toContain('instanceName');
    put.flush(null);

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET')
      .flush({ limite: 2, podeAdicionar: true, itens: [conexao({ id: 4, nome: 'Vendas Centro' })] });
  });

  // ==================================================================== conferência
  it('AO ABRIR, A TELA MOSTRA O STATUS CONFERIDO NA EVOLUTION, NÃO O DO ÚLTIMO AVISO', () => {
    // ===================== O DEFEITO =====================
    // A lista vinha só do banco, e o banco só sabia o que o último webhook contou. O número caiu
    // com a API desligada, o aviso se perdeu, e esta tela disse "Conectado" por seis dias.
    // =====================================================
    montar(
      { limite: 1, podeAdicionar: false, itens: [conexao({ status: 'conectado' })] },
      { limite: 1, podeAdicionar: false,
        itens: [conexao({ status: 'desconectado', desconectadoEm: '2026-10-07T21:00:00Z' })] });

    expect(c.lista()[0].status).toBe('desconectado');
    expect(texto()).toContain('Desconectado');
  });

  it('A CONFERÊNCIA É UMA SÓ: recarregar depois de uma ação não confere de novo', () => {
    // Cada conferência é um GET na Evolution por número. Ela vale ao ABRIR; depois disso quem
    // corrige é a conferência periódica do servidor.
    montar({ limite: 2, podeAdicionar: true, itens: [conexao()] });

    c.carregar();
    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET')
      .flush({ limite: 2, podeAdicionar: true, itens: [conexao()] });

    http.expectNone(r => r.url.endsWith('/conferir'));
  });

  it('a conferência que falha não apaga a lista do banco', () => {
    fixture = TestBed.createComponent(Conexao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET')
      .flush({ limite: 1, podeAdicionar: false, itens: [conexao({ nome: 'Vendas' })] });
    http.expectOne(r => r.url.endsWith('/conexoes/conferir'))
      .flush({ erro: 'Evolution fora do ar' }, { status: 502, statusText: 'Bad Gateway' });
    fixture.detectChanges();

    expect(c.lista().length).toBe(1);
    expect(c.erro()).toBe('');
  });

  // ==================================================================== polling
  it('NÃO HÁ POLLING DE STATUS ENQUANTO NENHUM QR ESTÁ NA TELA', () => {
    // ===================== O CUSTO QUE MULTI-NÚMERO CRIOU =====================
    // A tela antiga consultava o estado ao vivo a cada 3s, sempre. Com N números isso vira N
    // requisições por tick — e cada uma é um GET na Evolution, por instância. O poll passou a
    // existir só durante o pareamento, que é a única situação em que 3s se justificam.
    // =========================================================================
    jasmine.clock().install();
    try {
      montar({
        limite: 3, podeAdicionar: true,
        itens: [conexao({ id: 1 }), conexao({ id: 2, nome: 'Suporte' }), conexao({ id: 3, nome: 'Loja' })]
      });

      // Abrir os detalhes de uma conexão conectada pede a SAÚDE, não o status ao vivo.
      c.abrir(c.lista()[0]);
      http.expectOne(r => r.url.endsWith('/conexoes/1/saude')).flush(
        { enviadasHoje: 0, pendentes: 0, expiradas: 0, falhasHoje: 0 });

      jasmine.clock().tick(10_000);
      http.expectNone(r => r.url.includes('/status'));
      // Número por QR code não tem template: a lista nem é pedida (INT-XX).
      fixture.detectChanges();
      http.expectNone(r => r.url.includes('/modelos'));
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('o polling começa com o QR e para assim que conecta', () => {
    jasmine.clock().install();
    try {
      montar({
        limite: 2, podeAdicionar: true,
        itens: [conexao({ id: 5, nome: 'Nova', numero: null, status: 'nao_criada' })]
      });

      c.abrir(c.lista()[0]);
      http.expectOne(r => r.url.endsWith('/conexoes/5/saude')).flush(
        { enviadasHoje: 0, pendentes: 0, expiradas: 0, falhasHoje: 0 });

      c.gerarQr(5);
      http.expectOne(r => r.url.endsWith('/conexoes/5/conectar') && r.method === 'POST')
        .flush({ base64: 'data:image/png;base64,xx', codigo: null, pairingCode: null,
                 estado: 'connecting', conectado: false });

      // O primeiro status sai junto, sem esperar o tick — a tela não pode ficar 3s em branco.
      http.expectOne(r => r.url.endsWith('/conexoes/5/status'))
        .flush({ instanceName: 'emp-1-5', estado: 'connecting', conectado: false });

      jasmine.clock().tick(3_000);
      // Leu o QR: conectou.
      http.expectOne(r => r.url.endsWith('/conexoes/5/status'))
        .flush({ instanceName: 'emp-1-5', estado: 'open', conectado: true });

      // Conectar recarrega a lista e DESLIGA o poll.
      http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET')
        .flush({ limite: 2, podeAdicionar: true, itens: [conexao({ id: 5, nome: 'Nova' })] });

      jasmine.clock().tick(30_000);
      http.expectNone(r => r.url.includes('/status'));
    } finally {
      jasmine.clock().uninstall();
    }
  });

  // ==================================================================== troca de chip
  it('o aviso de troca de número fica NA LINHA da conexão que trocou', () => {
    // Com N números, um aviso solto no topo não diria qual deles mudou de chip.
    montar({
      limite: 2, podeAdicionar: true,
      itens: [
        conexao({ id: 1, nome: 'Vendas' }),
        conexao({ id: 2, nome: 'Suporte', numeroAnterior: '5584911112222' })
      ]
    });

    const linhas = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.conexoes > li')];
    expect(linhas.length).toBe(2);
    expect(linhas[0].querySelector('.troca')).toBeNull();
    expect(linhas[1].querySelector('.troca')?.textContent).toContain('5584911112222');
  });
});
