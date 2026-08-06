import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Conexao as ConexaoModel, Conexoes as ConexoesDto } from '../../nucleo/modelos';
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
      ...over
    };
  }

  let http: HttpTestingController;
  let fixture: ComponentFixture<Conexao>;
  let c: Conexao;

  function montar(resposta: ConexoesDto) {
    fixture = TestBed.createComponent(Conexao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conexoes') && r.method === 'GET').flush(resposta);
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
    expect(post.request.body).toEqual({ nome: 'Suporte' });
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
