import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { rotaFalsa } from '../telas-do-painel';
import { Importar } from './importar';

/** IMPORTAR LEADS DE UM ARQUIVO (INT-XX) — a tela que absorveu o modal da issue #8.
 *
 *  O que ela tem de garantir é sempre o mesmo: o que o dono vê é o que vai acontecer, e o que ele
 *  escolhe é o que o servidor recebe. Os números, os padrões e as recusas são do servidor; aqui só
 *  se prova que a tela os obedece e os manda de volta inteiros. */
describe('importar leads', () => {
  let http: HttpTestingController;

  const RECEBIDA = {
    id: 42,
    nomeArquivo: 'leads.csv',
    totalLinhas: 3,
    mapeamento: [
      { coluna: 'id', campo: 'meta_lead_id' },
      { coluna: 'full_name', campo: 'nome' },
      { coluna: 'phone_number', campo: 'telefone' },
      { coluna: 'Qual seu orçamento?', campo: 'ignorar' }
    ]
  };

  const PREVIA = {
    total: 3, novos: 2, duplicados: 1, invalidos: 0,
    primeiras: [
      { linha: 2, nome: 'Maria', telefone: '5584988887777', email: null, metaLeadId: '1001',
        criadoEm: '2026-09-10T14:32:11Z', resultado: 'importado', motivo: null },
      { linha: 3, nome: 'João', telefone: '5584999996666', email: null, metaLeadId: '1002',
        criadoEm: null, resultado: 'duplicado', motivo: 'telefone_ja_cadastrado' }
    ],
    aviso: { disponivel: true, marcadoPorPadrao: true }
  };

  function montar(papel: 'dono' | 'gestor' = 'dono') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@a.com', papel,
        permissoes: PERMISSOES_DE[papel], empresaNome: 'X'
      }
    } as never);

    TestBed.inject(PipelinesServico).lista.set([
      { id: 9, nome: 'Vendas', cor: '#7FA88B', ordem: 1, padrao: true, etapas: 3, contatos: 0 }
    ]);

    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(Importar);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => TestBed.resetTestingModule());

  /** O `change` do `<input type="file">`, sem tocar em disco. */
  function eventoComArquivo(nome = 'leads.csv'): Event {
    const dt = new DataTransfer();
    dt.items.add(new File(['id,full_name,phone_number\n1,Maria,+5584988887777'], nome,
      { type: 'text/csv' }));

    const input = document.createElement('input');
    input.type = 'file';
    input.files = dt.files;
    return { target: input } as unknown as Event;
  }

  /** Sobe o arquivo e responde com o mapeamento sugerido. */
  function subir(fixture: ComponentFixture<Importar>, resposta: object = RECEBIDA) {
    fixture.componentInstance.escolherArquivo(eventoComArquivo());
    http.expectOne(r => r.url.endsWith('/importacoes') && r.method === 'POST').flush(resposta);
    // O dono carrega a equipe para o seletor de responsável.
    http.match(r => r.url.includes('/equipe')).forEach(r => r.flush([]));
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  /** ⚠️ O UPLOAD NÃO GRAVA NADA, e a tela tem de deixar isso claro: ela vai para o MAPEAMENTO, e
   *  não para um resultado. Se ela pulasse direto para "pronto", o dono descobriria o que o
   *  sistema entendeu das colunas dele depois de 10.000 contatos entrarem. */
  it('SUBIR O ARQUIVO LEVA AO MAPEAMENTO, E NÃO GRAVA NADA', () => {
    const fixture = montar();
    const c = subir(fixture);

    expect(c.passo()).toBe('mapear');
    // Nenhum pedido de gravação saiu.
    expect(http.match(r => r.url.includes('/gravar')).length).toBe(0);

    // Uma linha por coluna do arquivo, com o que o servidor sugeriu já ligado.
    const linhas = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')];
    expect(linhas.length).toBe(4);
    expect(linhas[0].textContent).toContain('id');
  });

  /** ⚠️ SEM TELEFONE NÃO DÁ PARA CONFERIR: é por ele que o servidor sabe quem já está na base, e
   *  o clique voltaria recusado. A tela não decide a regra — ela evita o clique que já se sabe
   *  que erra, e diz por quê. */
  it('SEM COLUNA DE TELEFONE, CONFERIR FICA BLOQUEADO', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.ligar('phone_number', 'ignorar');
    fixture.detectChanges();

    const conferir = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
      .find(b => b.textContent!.trim() === 'Conferir')!;

    expect(conferir.disabled).withContext('sem telefone o avanço tem de travar').toBeTrue();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('qual coluna é o');

    c.ligar('phone_number', 'telefone');
    fixture.detectChanges();
    expect(conferir.disabled).toBeFalse();
  });

  /** O mapeamento que a tela manda é o que está na tela — inclusive o que o dono mudou. Foi o
   *  ponto do passo: a coluna que o cliente criou no formulário ("Qual seu orçamento?") só vira
   *  observação porque alguém disse que ela é isso. */
  it('CONFERIR MANDA O MAPEAMENTO COMO ESTÁ NA TELA', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.ligar('Qual seu orçamento?', 'observacoes');
    c.conferir();

    const pedido = http.expectOne(r => r.url.endsWith('/importacoes/42/previa'));
    const corpo = pedido.request.body as { mapeamento: { coluna: string; campo: string }[] };

    expect(corpo.mapeamento.find(m => m.coluna === 'Qual seu orçamento?')!.campo).toBe('observacoes');
    pedido.flush(PREVIA);
  });

  /** ⚠️ TROCAR UMA COLUNA JOGA A PRÉVIA FORA. Ela descreve o mapeamento anterior, e deixá-la na
   *  tela seria oferecer "Importar 2" sobre números que já não são daquele mapeamento — e o
   *  clique gravaria com o novo. */
  it('MEXER NO MAPEAMENTO DESCARTA A PRÉVIA', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa')).flush(PREVIA);
    fixture.detectChanges();
    expect(c.previa()).not.toBeNull();

    c.ligar('full_name', 'ignorar');
    fixture.detectChanges();

    expect(c.previa()).withContext('a prévia do mapeamento antigo ficou na tela').toBeNull();
  });

  /** A caixinha do aviso é do SERVIDOR: ele diz se ela aparece e como chega. No arquivo da Meta
   *  ela vem marcada — o lead é de ontem, e a automação do cliente é o que ele quer que rode. */
  it('O AVISO ÀS INTEGRAÇÕES VEM COMO O SERVIDOR MANDA', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa')).flush(PREVIA);
    fixture.detectChanges();

    expect(c.avisarIntegracoes()).withContext('o padrão do servidor foi ignorado').toBeTrue();
    expect((fixture.nativeElement as HTMLElement).querySelector('#aviso-integracao')).not.toBeNull();

    // E sem integração ouvindo, a caixinha nem aparece.
    c.ligar('full_name', 'nome');
    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa'))
      .flush({ ...PREVIA, aviso: { disponivel: false, marcadoPorPadrao: false } });
    fixture.detectChanges();

    expect(c.avisarIntegracoes()).toBeFalse();
    expect((fixture.nativeElement as HTMLElement).querySelector('#aviso-integracao')).toBeNull();
  });

  /** ⚠️ O QUE O DONO ESCOLHEU VAI INTEIRO NO PEDIDO: mapeamento, funil, responsável e aviso. O
   *  funil nulo é o padrão — importar 10.000 leads direto para o quadro é um quadro inutilizável. */
  it('IMPORTAR MANDA AS ESCOLHAS DO DONO', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa')).flush(PREVIA);
    fixture.detectChanges();

    expect(c.funil()).withContext('o padrão deixou de ser "sem funil"').toBeNull();

    c.funil.set(9);
    c.responsavel.set(7);
    c.importar();

    const pedido = http.expectOne(r => r.url.endsWith('/importacoes/42/gravar'));
    const corpo = pedido.request.body as {
      pipelineId: number; responsavelId: number; avisarIntegracoes: boolean;
      mapeamento: unknown[];
    };

    expect(corpo.pipelineId).toBe(9);
    expect(corpo.responsavelId).toBe(7);
    expect(corpo.avisarIntegracoes).toBeTrue();
    expect(corpo.mapeamento.length).toBe(4);

    pedido.flush({ id: 42, total: 3, importados: 2, duplicados: 1, invalidos: 0, status: 'concluida' });
    fixture.detectChanges();

    expect(c.passo()).toBe('fim');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('2');
  });

  /** ⚠️ O ARQUIVO GRANDE É GRAVADO POR UM JOB, e a tela PERGUNTA onde ele está. Sem isso ela
   *  ficaria parada em "importando" para sempre — e quem está olhando concluiria que travou e
   *  clicaria de novo, o que a essa altura seria uma segunda importação. */
  it('COM O ARQUIVO GRANDE, A TELA ACOMPANHA ATÉ TERMINAR', () => {
    // ⚠️ `jasmine.clock`, e NÃO `fakeAsync`: o painel é zoneless, e `fakeAsync` depende do
    // zone-testing — sem ele o helper lança e derruba o navegador inteiro, num erro que não se
    // parece com a causa. O componente usa `setInterval` puro, que este relógio controla.
    jasmine.clock().install();

    const fixture = montar();
    const c = subir(fixture, { ...RECEBIDA, totalLinhas: 900 });

    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa')).flush({ ...PREVIA, total: 900, novos: 900 });
    fixture.detectChanges();

    c.importar();
    http.expectOne(r => r.url.endsWith('/gravar'))
      .flush({ id: 42, total: 900, importados: 0, duplicados: 0, invalidos: 0, status: 'processando' });
    fixture.detectChanges();

    expect(c.passo()).toBe('fim');
    expect(c.resultado()!.status).toBe('processando');

    // Primeira pergunta: ainda processando, e o número ANDOU.
    jasmine.clock().tick(2000);
    http.expectOne(r => r.url.endsWith('/importacoes/42') && r.method === 'GET')
      .flush({ id: 42, total: 900, importados: 500, duplicados: 0, invalidos: 0, status: 'processando' });
    fixture.detectChanges();
    expect(c.resultado()!.importados).toBe(500);

    // Segunda: terminou.
    jasmine.clock().tick(2000);
    http.expectOne(r => r.url.endsWith('/importacoes/42') && r.method === 'GET')
      .flush({ id: 42, total: 900, importados: 900, duplicados: 0, invalidos: 0, status: 'concluida' });
    fixture.detectChanges();
    expect(c.resultado()!.status).toBe('concluida');

    // E para de perguntar quando acaba — senão a tela bate na API para sempre.
    jasmine.clock().tick(4000);
    expect(http.match(r => r.url.endsWith('/importacoes/42')).length).toBe(0);

    jasmine.clock().uninstall();
  });

  /** Recomeçar volta ao primeiro passo com tudo limpo: outro arquivo é outra importação, e
   *  herdar o funil ou o aviso do anterior seria decidir pelo dono. */
  it('RECOMEÇAR LIMPA A TELA INTEIRA', () => {
    const fixture = montar();
    const c = subir(fixture);

    c.conferir();
    http.expectOne(r => r.url.endsWith('/previa')).flush(PREVIA);
    c.funil.set(9);
    fixture.detectChanges();

    c.recomecar();
    fixture.detectChanges();

    expect(c.passo()).toBe('arquivo');
    expect(c.previa()).toBeNull();
    expect(c.funil()).toBeNull();
    expect(c.avisarIntegracoes()).toBeFalse();
  });

  /** A recusa do servidor aparece na tela com a frase dele — "o arquivo não tem a coluna X" diz o
   *  que fazer; "erro ao importar" não diz nada. */
  it('A RECUSA DO SERVIDOR APARECE NA TELA', () => {
    const fixture = montar();
    fixture.componentInstance.escolherArquivo(eventoComArquivo('grande.csv'));

    http.expectOne(r => r.url.endsWith('/importacoes')).flush(
      { erro: 'Arquivo grande demais (máximo 10 MB). Divida o export em partes.' },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Divida o export em partes');
    expect(fixture.componentInstance.passo()).toBe('arquivo');
  });
});
