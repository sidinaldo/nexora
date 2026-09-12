import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PipelineDto } from '../../nucleo/modelos';
import { Pipelines } from './pipelines';

/** A TELA DE FUNIS.
 *
 *  ⚠️ ESTE ARQUIVO NASCEU DE UM DEFEITO QUE OS TESTES NÃO ACHARAM. O formulário de criar/editar
 *  usa `ngTemplateOutlet`, e o componente não importava a diretiva — o Angular só AVISA no build
 *  e simplesmente não renderiza nada. Clicar em "+ Novo funil" não abria nada, em silêncio, e a
 *  suíte inteira continuava verde porque ninguém olhava para o formulário.
 *
 *  Daí o primeiro teste daqui ser "o formulário abre": é barato e cobre uma classe de falha que
 *  nenhum teste genérico de renderização pega. */
describe('funis', () => {
  const LISTA: PipelineDto[] = [
    { id: 1, nome: 'Vendas', cor: '#2E7A56', ordem: 1, padrao: true, etapas: 5, contatos: 4 },
    { id: 2, nome: 'Pós-venda', cor: '#A97A22', ordem: 2, padrao: false, etapas: 3, contatos: 2 }
  ];

  let componente: Pipelines;
  let http: HttpTestingController;
  let fixture: ComponentFixture<Pipelines>;

  function montar(lista: PipelineDto[] = LISTA) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    fixture = TestBed.createComponent(Pipelines);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);

    fixture.detectChanges();
    http.expectOne(r => r.url.includes('/pipelines')).flush(lista);
    fixture.detectChanges();
    return fixture;
  }

  function raiz(): HTMLElement { return fixture.nativeElement as HTMLElement; }

  afterEach(() => TestBed.resetTestingModule());

  it('O FORMULÁRIO ABRE DE VERDADE', () => {
    // O defeito que originou o arquivo: `ngTemplateOutlet` sem import não renderiza e não falha.
    montar();
    componente.abrirNovo();
    fixture.detectChanges();

    expect(raiz().querySelector('.pipeline-form'))
      .withContext('o <ng-template> do formulário precisa ter sido instanciado').not.toBeNull();
    expect(raiz().querySelector('#nome-pipeline')).not.toBeNull();
  });

  it('O MESMO FORMULÁRIO SERVE PARA EDITAR, JÁ PREENCHIDO', async () => {
    montar();
    componente.editar(LISTA[1]);
    fixture.detectChanges();
    // `ngModel` escreve no input de forma assíncrona — sem esperar, o `.value` ainda está vazio
    // e o teste mediria o instante antes da escrita.
    await fixture.whenStable();
    fixture.detectChanges();

    const campo = raiz().querySelector('#nome-pipeline') as HTMLInputElement;
    expect(campo.value).toBe('Pós-venda');
    expect(componente.fCor()).toBe('#A97A22');
  });

  // ==================================================================== a regra do padrão
  it('O FUNIL PADRÃO NÃO OFERECE O BOTÃO DE APAGAR', () => {
    // ===================== A INVARIANTE QUE O BANCO NÃO GARANTE =====================
    // Todo lead novo entra pela pipeline padrão. Sem ela, a próxima mensagem de número
    // desconhecido não teria onde criar contato — e o erro apareceria no processamento do
    // webhook, longe de quem apagou.
    //
    // A API recusa de qualquer forma; a tela esconde o botão para o dono não descobrir a regra
    // levando erro depois do clique.
    // ===============================================================================
    montar();
    expect(componente.podeApagar(LISTA[0])).withContext('é a padrão').toBeFalse();
    expect(componente.podeApagar(LISTA[1])).toBeTrue();
  });

  it('COM UM FUNIL SÓ, NINGUÉM PODE APAGÁ-LO', () => {
    montar([LISTA[0]]);
    expect(componente.podeApagar(LISTA[0])).toBeFalse();
  });

  it('SÓ O NÃO-PADRÃO OFERECE "TORNAR PADRÃO"', () => {
    montar();
    const rotulos = [...raiz().querySelectorAll('button')]
      .map(b => b.getAttribute('aria-label'))
      .filter((r): r is string => !!r && r.startsWith('Tornar'));

    expect(rotulos).toEqual(['Tornar Pós-venda a pipeline padrão']);
  });

  // ==================================================================== teto
  it('NO TETO O BOTÃO DE CRIAR FICA INDISPONÍVEL, COM O MOTIVO NO TÍTULO', () => {
    const cheia = Array.from({ length: 5 }, (_, i) => (
      { id: i + 1, nome: `Funil ${i}`, cor: '#2E7A56', ordem: i + 1, padrao: i === 0, etapas: 2, contatos: 0 }));
    montar(cheia);

    const botao = [...raiz().querySelectorAll('button')]
      .find(b => b.textContent?.includes('Nova pipeline')) as HTMLButtonElement;

    expect(botao.disabled).toBeTrue();
    expect(botao.title).toContain('Apague alguma');
  });

  // ==================================================================== erro do servidor
  it('O NOME REPETIDO APARECE ABAIXO DO CAMPO, E O FORMULÁRIO FICA ABERTO', () => {
    montar();
    componente.abrirNovo();
    componente.fNome.set('Vendas');
    componente.salvar();

    http.expectOne(r => r.method === 'POST').flush(
      { erro: 'Já existe um funil chamado "Vendas".' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(raiz().querySelector('.campo-erro')?.textContent).toContain('Já existe');
    expect(componente.editando())
      .withContext('fechar obrigaria a redigitar tudo para corrigir uma letra').toBe('novo');
  });

  it('O DUPLO CLIQUE NÃO CRIA DUAS VEZES', () => {
    montar();
    componente.abrirNovo();
    componente.fNome.set('Atacado');
    componente.salvar();
    componente.salvar();

    http.expectOne(r => r.method === 'POST').flush({ id: 9 });
  });

  // ==================================================================== navegação
  it('CADA FUNIL LEVA AO QUADRO E ÀS ETAPAS DELE', () => {
    // Com etapas por pipeline, "Etapas do funil" deixou de ser uma tela única — é daqui que se
    // chega às de cada um.
    montar();
    const hrefs = [...raiz().querySelectorAll('a')].map(a => a.getAttribute('href'));

    expect(hrefs).toContain('/crm/1');
    expect(hrefs).toContain('/crm/1/etapas');
    expect(hrefs).toContain('/crm/2');
    expect(hrefs).toContain('/crm/2/etapas');
  });
});
